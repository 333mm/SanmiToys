using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SanmiToys.Modules.SwiftVolume.Models;

namespace SanmiToys.Modules.SwiftVolume.Core;

/// <summary>
/// WASAPI Event-Driven 方式による超低遅延（約10〜15ms）マイクモニタリング（サイドトーン）エンジン
/// </summary>
public class MicMonitoringEngine : IDisposable
{
    private readonly Func<SwiftVolumeSettings> _settingsAccessor;
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private LowLatencyMonitorWaveProvider? _waveProvider;
    private readonly object _stateLock = new();
    private bool _isStarting;
    private bool _isDisposed;

    public bool IsRunning { get; private set; }

    public event Action<bool>? StateChanged;

    public MicMonitoringEngine(Func<SwiftVolumeSettings> settingsAccessor)
    {
        _settingsAccessor = settingsAccessor;
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (IsRunning || _isStarting || _isDisposed) return;
            _isStarting = true;
        }

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                InitializeAudioPipeline();
                lock (_stateLock)
                {
                    IsRunning = true;
                    _isStarting = false;
                }
                StateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MicMonitoringEngine] Start failed: {ex}");
                StopInternal();
                lock (_stateLock)
                {
                    _isStarting = false;
                }
                StateChanged?.Invoke(false);
            }
        });
    }

    public void Stop()
    {
        StopInternal();
        StateChanged?.Invoke(false);
    }

    private void StopInternal()
    {
        lock (_stateLock)
        {
            IsRunning = false;
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnCaptureDataAvailable;
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }
            }
            catch { }

            try
            {
                if (_output != null)
                {
                    _output.Stop();
                    _output.Dispose();
                    _output = null;
                }
            }
            catch { }

            _waveProvider?.Reset();
            _waveProvider = null;
        }
    }

    public void Restart()
    {
        Stop();
        var settings = _settingsAccessor();
        if (settings.EnableMicMonitoring)
        {
            Start();
        }
    }

    public void UpdateVolume(float volumeScalar)
    {
        if (_waveProvider != null)
        {
            _waveProvider.Volume = Math.Clamp(volumeScalar, 0f, 1.0f);
        }
    }

    private void InitializeAudioPipeline()
    {
        var settings = _settingsAccessor();
        using var enumerator = new MMDeviceEnumerator();

        // 1. 入力マイクデバイスの解決
        MMDevice? inputDevice = null;
        if (!string.IsNullOrEmpty(settings.MicMonitoringInputDeviceId))
        {
            try { inputDevice = enumerator.GetDevice(settings.MicMonitoringInputDeviceId); } catch { }
        }
        if (inputDevice == null)
        {
            try { inputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { }
        }
        if (inputDevice == null)
        {
            try { inputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); } catch { }
        }
        if (inputDevice == null) throw new InvalidOperationException("No audio capture device found.");

        // 2. 出力デバイスの解決
        MMDevice? outputDevice = null;
        if (!string.IsNullOrEmpty(settings.MicMonitoringOutputDeviceId))
        {
            try { outputDevice = enumerator.GetDevice(settings.MicMonitoringOutputDeviceId); } catch { }
        }
        if (outputDevice == null)
        {
            try { outputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
        }
        if (outputDevice == null) throw new InvalidOperationException("No audio render device found.");

        // 3. 超低遅延 WASAPI Event-Driven Capture & Out の構成 (レイテンシ約 12ms)
        const int targetLatencyMs = 12;

        _capture = new WasapiCapture(inputDevice, true, targetLatencyMs);
        var inputFormat = _capture.WaveFormat;
        var outputMixFormat = outputDevice.AudioClient.MixFormat;

        float initialVol = settings.MicMonitoringVolumePercent / 100f;
        _waveProvider = new LowLatencyMonitorWaveProvider(inputFormat, outputMixFormat)
        {
            Volume = Math.Clamp(initialVol, 0f, 1.0f)
        };

        _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, targetLatencyMs);
        _output.Init(_waveProvider);

        _capture.DataAvailable += OnCaptureDataAvailable;

        _output.Play();
        _capture.StartRecording();
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _waveProvider == null || _capture == null) return;
        _waveProvider.AddRecordedBytes(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
        StopInternal();
    }
}

/// <summary>
/// マイク入力を出力先（Sound BlasterX G5等のマルチチャンネルや通常ステレオ）へ遅延蓄積なしに直接バイト出力する IWaveProvider
/// </summary>
public class LowLatencyMonitorWaveProvider : IWaveProvider
{
    private readonly WaveFormat _inputFormat;
    private readonly WaveFormat _outputFormat;
    private readonly int _inputChannels;
    private readonly int _outputChannels;
    private readonly int _outputSampleRate;
    private readonly int _outputBitsPerSample;
    private readonly bool _outputIsFloat;

    // 最新のインターリーブされたステレオ float サンプルを蓄積する極小リングバッファ
    private readonly float[] _ringBuffer;
    private readonly int _ringCapacity;
    private int _writePos;
    private int _readPos;
    private int _availableFrames; // ステレオフレーム単位 (L+R で 1フレーム)
    private readonly object _bufferLock = new();

    public float Volume { get; set; } = 1.0f;
    public WaveFormat WaveFormat => _outputFormat;

    public LowLatencyMonitorWaveProvider(WaveFormat inputFormat, WaveFormat outputFormat)
    {
        _inputFormat = inputFormat;
        _outputFormat = outputFormat;
        _inputChannels = Math.Max(1, inputFormat.Channels);
        _outputChannels = Math.Max(1, outputFormat.Channels);
        _outputSampleRate = outputFormat.SampleRate;
        _outputBitsPerSample = outputFormat.BitsPerSample;

        _outputIsFloat = outputFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                         (outputFormat.Encoding == WaveFormatEncoding.Extensible && outputFormat.BitsPerSample == 32);

        // 最大バッファ長: 48kHz で約 30ms (1440 フレーム = 2880 floats)
        int targetMaxFrames = Math.Max(512, (int)(_outputSampleRate * 0.030));
        _ringCapacity = targetMaxFrames * 2; // ステレオ (L, R)
        _ringBuffer = new float[_ringCapacity];
    }

    public void Reset()
    {
        lock (_bufferLock)
        {
            _writePos = 0;
            _readPos = 0;
            _availableFrames = 0;
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
        }
    }

    public void AddRecordedBytes(byte[] rawBuffer, int offset, int count)
    {
        if (count <= 0) return;

        int bytesPerSample = _inputFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0) bytesPerSample = 2;
        int frameSize = bytesPerSample * _inputChannels;
        int inputFrames = count / frameSize;
        if (inputFrames <= 0) return;

        bool isFloat = _inputFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (_inputFormat.Encoding == WaveFormatEncoding.Extensible && _inputFormat.BitsPerSample == 32);

        lock (_bufferLock)
        {
            int ptr = offset;

            for (int i = 0; i < inputFrames; i++)
            {
                float left = 0f;
                float right = 0f;

                if (isFloat && bytesPerSample == 4)
                {
                    left = BitConverter.ToSingle(rawBuffer, ptr);
                    right = _inputChannels > 1 ? BitConverter.ToSingle(rawBuffer, ptr + 4) : left;
                }
                else if (bytesPerSample == 2)
                {
                    short sLeft = BitConverter.ToInt16(rawBuffer, ptr);
                    left = sLeft / 32768.0f;
                    if (_inputChannels > 1)
                    {
                        short sRight = BitConverter.ToInt16(rawBuffer, ptr + 2);
                        right = sRight / 32768.0f;
                    }
                    else
                    {
                        right = left;
                    }
                }
                else if (bytesPerSample == 3)
                {
                    int valLeft = (rawBuffer[ptr] << 8) | (rawBuffer[ptr + 1] << 16) | (rawBuffer[ptr + 2] << 24);
                    left = valLeft / 2147483648.0f;
                    if (_inputChannels > 1)
                    {
                        int valRight = (rawBuffer[ptr + 3] << 8) | (rawBuffer[ptr + 4] << 16) | (rawBuffer[ptr + 5] << 24);
                        right = valRight / 2147483648.0f;
                    }
                    else
                    {
                        right = left;
                    }
                }
                ptr += frameSize;

                WriteStereoFrame(left, right);
            }

            // バッファが過剰（35ms分超）に溜まった場合は古いフレームをスキップして遅延を常に最小化（遅延累積防止）
            int maxLatencyFrames = (int)(_outputSampleRate * 0.035);
            if (_availableFrames > maxLatencyFrames)
            {
                int skip = _availableFrames - maxLatencyFrames;
                _readPos = (_readPos + skip * 2) % _ringCapacity;
                _availableFrames = maxLatencyFrames;
            }
        }
    }

    private void WriteStereoFrame(float left, float right)
    {
        _ringBuffer[_writePos] = left;
        _ringBuffer[_writePos + 1] = right;
        _writePos = (_writePos + 2) % _ringCapacity;

        if (_availableFrames * 2 < _ringCapacity)
        {
            _availableFrames++;
        }
        else
        {
            _readPos = (_readPos + 2) % _ringCapacity;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesPerSample = _outputBitsPerSample / 8;
        if (bytesPerSample <= 0) bytesPerSample = 4;
        int frameSize = bytesPerSample * _outputChannels;
        int totalFramesRequested = count / frameSize;
        if (totalFramesRequested <= 0) return 0;

        float vol = Volume;

        lock (_bufferLock)
        {
            int framesToRead = Math.Min(totalFramesRequested, _availableFrames);
            int outByteIdx = offset;

            for (int f = 0; f < framesToRead; f++)
            {
                float left = _ringBuffer[_readPos] * vol;
                float right = _ringBuffer[_readPos + 1] * vol;
                _readPos = (_readPos + 2) % _ringCapacity;

                if (_outputIsFloat && bytesPerSample == 4)
                {
                    // 32-bit Float 出力 (Windows 共有モード標準、Sound BlasterX G5 等)
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(outByteIdx), left);
                    if (_outputChannels > 1)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(outByteIdx + 4), right);
                    }
                    // サラウンドチャンネル (Center, Subwoofer, Side/Rear 等) は無音
                    for (int c = 2; c < _outputChannels; c++)
                    {
                        Array.Clear(buffer, outByteIdx + c * 4, 4);
                    }
                }
                else if (bytesPerSample == 2)
                {
                    // 16-bit PCM 出力
                    short sLeft = (short)Math.Clamp((int)(left * 32767f), -32768, 32767);
                    short sRight = (short)Math.Clamp((int)(right * 32767f), -32768, 32767);

                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(outByteIdx), sLeft);
                    if (_outputChannels > 1)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(outByteIdx + 2), sRight);
                    }
                    for (int c = 2; c < _outputChannels; c++)
                    {
                        Array.Clear(buffer, outByteIdx + c * 2, 2);
                    }
                }
                else
                {
                    Array.Clear(buffer, outByteIdx, frameSize);
                }

                outByteIdx += frameSize;
            }
            _availableFrames -= framesToRead;

            // マイクデータ不足時は無音フィル
            int remainingFrames = totalFramesRequested - framesToRead;
            if (remainingFrames > 0)
            {
                Array.Clear(buffer, outByteIdx, remainingFrames * frameSize);
            }
        }

        return count;
    }
}
