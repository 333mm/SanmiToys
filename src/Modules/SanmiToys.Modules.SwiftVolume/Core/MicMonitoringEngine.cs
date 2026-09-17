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
                try
                {
                    using var currentProc = Process.GetCurrentProcess();
                    if (currentProc.PriorityClass == ProcessPriorityClass.Normal)
                    {
                        currentProc.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                }
                catch { }

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

    public void UpdateNoiseGate(bool enabled, int thresholdDb)
    {
        _waveProvider?.SetNoiseGate(enabled, thresholdDb);
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

        // 3. 超低遅延 WASAPI Event-Driven Capture & Out の構成 (レイテンシ約 20ms: アプリ起動時等の負荷変動にも強い安定値)
        const int targetLatencyMs = 20;

        _capture = new WasapiCapture(inputDevice, true, targetLatencyMs);
        var inputFormat = _capture.WaveFormat;
        var outputMixFormat = outputDevice.AudioClient.MixFormat;

        float initialVol = settings.MicMonitoringVolumePercent / 100f;
        _waveProvider = new LowLatencyMonitorWaveProvider(inputFormat, outputMixFormat)
        {
            Volume = Math.Clamp(initialVol, 0f, 1.0f)
        };
        _waveProvider.SetNoiseGate(settings.EnableMicNoiseGate, settings.MicNoiseGateThresholdDb);

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
/// マイク入力を出力先（Sound BlasterX G5等のマルチチャンネルや96kHz/192kHzハイレゾ出力）へ
/// サンプルレート変換（Hermite 3次多項式補間）と遅延蓄積防止を行って出力する超低遅延 IWaveProvider
/// </summary>
public class LowLatencyMonitorWaveProvider : IWaveProvider
{
    private readonly WaveFormat _inputFormat;
    private readonly WaveFormat _outputFormat;
    private readonly int _inputChannels;
    private readonly int _outputChannels;
    private readonly int _inputSampleRate;
    private readonly int _outputSampleRate;
    private readonly int _outputBitsPerSample;
    private readonly bool _outputIsFloat;
    private readonly bool _needsResampling;
    private readonly double _resampleStep; // inputSampleRate / outputSampleRate

    // 入力デコード & リサンプラー用バッファ（マイクスレッド専用・ロック不要）
    private float[] _workBufferL = new float[2048];
    private float[] _workBufferR = new float[2048];
    private float[] _resampledOutL = new float[2048];
    private float[] _resampledOutR = new float[2048];
    private readonly float[] _historyL = new float[3];
    private readonly float[] _historyR = new float[3];
    private double _resampleT = 2.0;
    private bool _hasResampleHistory;

    // 最新のインターリーブされたステレオ float サンプルを蓄積するリングバッファ (120ms 分)
    private readonly float[] _ringBuffer;
    private readonly int _ringCapacity;
    private int _writePos;
    private int _readPos;
    private int _availableFrames; // ステレオフレーム単位 (L+R で 1フレーム)
    private readonly object _bufferLock = new();

    // アンダーラン時のクロスフェード用（再生スレッド専用）
    private float _lastOutL;
    private float _lastOutR;
    private bool _wasUnderflow;

    // ノイズゲートDSP制御
    private volatile bool _noiseGateEnabled;
    private float _gateOpenThreshold = 0.00562f; // -45dB
    private float _gateCloseThreshold = 0.00398f; // -48dB (-3dB hysteresis)
    private readonly float _attackStep;
    private readonly float _releaseStep;
    private readonly int _holdFrames;
    private float _gateCurrentGain = 1.0f;
    private int _gateHoldCounter;

    public float Volume { get; set; } = 1.0f;
    public WaveFormat WaveFormat => _outputFormat;

    public LowLatencyMonitorWaveProvider(WaveFormat inputFormat, WaveFormat outputFormat)
    {
        _inputFormat = inputFormat;
        _outputFormat = outputFormat;
        _inputChannels = Math.Max(1, inputFormat.Channels);
        _outputChannels = Math.Max(1, outputFormat.Channels);
        _inputSampleRate = Math.Max(8000, inputFormat.SampleRate);
        _outputSampleRate = Math.Max(8000, outputFormat.SampleRate);
        _outputBitsPerSample = outputFormat.BitsPerSample;

        _needsResampling = (_inputSampleRate != _outputSampleRate);
        _resampleStep = (double)_inputSampleRate / _outputSampleRate;

        _outputIsFloat = outputFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                         (outputFormat.Encoding == WaveFormatEncoding.Extensible && outputFormat.BitsPerSample == 32);

        // バッファ容量: 120ms 分のステレオフレームを保持できる余裕を確保 (96kHzでも約92KB)
        int ringFrames = Math.Max(1024, (int)(_outputSampleRate * 0.120));
        _ringCapacity = ringFrames * 2; // ステレオ (L, R)
        _ringBuffer = new float[_ringCapacity];

        // ノイズゲートパラメータの事前計算 (Attack 2ms, Release 50ms, Hold 150ms)
        _attackStep = 1.0f / Math.Max(1, (int)(0.002f * _outputSampleRate));
        _releaseStep = 1.0f / Math.Max(1, (int)(0.050f * _outputSampleRate));
        _holdFrames = (int)(0.150f * _outputSampleRate);
    }

    public void SetNoiseGate(bool enabled, int thresholdDb)
    {
        lock (_bufferLock)
        {
            _noiseGateEnabled = enabled;
            int clampedDb = Math.Clamp(thresholdDb, -80, 0);
            _gateOpenThreshold = MathF.Pow(10f, clampedDb / 20f);
            _gateCloseThreshold = _gateOpenThreshold * 0.7079f; // -3dB hysteresis
            if (!enabled)
            {
                _gateCurrentGain = 1.0f;
                _gateHoldCounter = 0;
            }
        }
    }

    public void Reset()
    {
        lock (_bufferLock)
        {
            _writePos = 0;
            _readPos = 0;
            _availableFrames = 0;
            _gateCurrentGain = _noiseGateEnabled ? 0.0f : 1.0f;
            _gateHoldCounter = 0;
            _hasResampleHistory = false;
            _resampleT = 2.0;
            _lastOutL = 0f;
            _lastOutR = 0f;
            _wasUnderflow = false;
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
            Array.Clear(_historyL, 0, _historyL.Length);
            Array.Clear(_historyR, 0, _historyR.Length);
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

        int generatedFrames = 0;

        // --- デコードおよびリサンプリング処理（再生スレッドのブロッキングを防ぐため lock の外で実行） ---
        if (!_needsResampling)
        {
            if (_resampledOutL.Length < inputFrames)
            {
                int newCap = Math.Max(inputFrames * 2, 2048);
                _resampledOutL = new float[newCap];
                _resampledOutR = new float[newCap];
            }

            int ptr = offset;
            for (int i = 0; i < inputFrames; i++)
            {
                float left = 0f;
                float right = 0f;

                if (isFloat && bytesPerSample == 4)
                {
                    left = BinaryPrimitives.ReadSingleLittleEndian(rawBuffer.AsSpan(ptr));
                    right = _inputChannels > 1 ? BinaryPrimitives.ReadSingleLittleEndian(rawBuffer.AsSpan(ptr + 4)) : left;
                }
                else if (bytesPerSample == 2)
                {
                    short sLeft = BinaryPrimitives.ReadInt16LittleEndian(rawBuffer.AsSpan(ptr));
                    left = sLeft / 32768.0f;
                    if (_inputChannels > 1)
                    {
                        short sRight = BinaryPrimitives.ReadInt16LittleEndian(rawBuffer.AsSpan(ptr + 2));
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

                _resampledOutL[i] = left;
                _resampledOutR[i] = right;
            }
            generatedFrames = inputFrames;
        }
        else
        {
            int totalWorkFrames = 3 + inputFrames;
            if (_workBufferL.Length < totalWorkFrames)
            {
                int newCap = Math.Max(totalWorkFrames * 2, 2048);
                _workBufferL = new float[newCap];
                _workBufferR = new float[newCap];
            }

            if (_hasResampleHistory)
            {
                _workBufferL[0] = _historyL[0];
                _workBufferL[1] = _historyL[1];
                _workBufferL[2] = _historyL[2];
                _workBufferR[0] = _historyR[0];
                _workBufferR[1] = _historyR[1];
                _workBufferR[2] = _historyR[2];
            }

            int ptr = offset;
            for (int i = 0; i < inputFrames; i++)
            {
                float left = 0f;
                float right = 0f;

                if (isFloat && bytesPerSample == 4)
                {
                    left = BinaryPrimitives.ReadSingleLittleEndian(rawBuffer.AsSpan(ptr));
                    right = _inputChannels > 1 ? BinaryPrimitives.ReadSingleLittleEndian(rawBuffer.AsSpan(ptr + 4)) : left;
                }
                else if (bytesPerSample == 2)
                {
                    short sLeft = BinaryPrimitives.ReadInt16LittleEndian(rawBuffer.AsSpan(ptr));
                    left = sLeft / 32768.0f;
                    if (_inputChannels > 1)
                    {
                        short sRight = BinaryPrimitives.ReadInt16LittleEndian(rawBuffer.AsSpan(ptr + 2));
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

                _workBufferL[3 + i] = left;
                _workBufferR[3 + i] = right;
            }

            if (!_hasResampleHistory)
            {
                float initialL = _workBufferL[3];
                float initialR = _workBufferR[3];
                _workBufferL[0] = initialL;
                _workBufferL[1] = initialL;
                _workBufferL[2] = initialL;
                _workBufferR[0] = initialR;
                _workBufferR[1] = initialR;
                _workBufferR[2] = initialR;
                _hasResampleHistory = true;
                _resampleT = 2.0;
            }

            double t = _resampleT;
            double step = _resampleStep;
            int limitIdx = inputFrames;

            int estOutputFrames = (int)Math.Ceiling((limitIdx - t + 1) / step) + 8;
            if (_resampledOutL.Length < estOutputFrames)
            {
                int newCap = Math.Max(estOutputFrames * 2, 2048);
                _resampledOutL = new float[newCap];
                _resampledOutR = new float[newCap];
            }

            int outIdx = 0;
            while (true)
            {
                int idx = (int)t;
                if (idx > limitIdx) break;

                float frac = (float)(t - idx);
                float outL = InterpolateHermite(_workBufferL[idx - 1], _workBufferL[idx], _workBufferL[idx + 1], _workBufferL[idx + 2], frac);
                float outR = InterpolateHermite(_workBufferR[idx - 1], _workBufferR[idx], _workBufferR[idx + 1], _workBufferR[idx + 2], frac);

                _resampledOutL[outIdx] = Math.Clamp(outL, -1.0f, 1.0f);
                _resampledOutR[outIdx] = Math.Clamp(outR, -1.0f, 1.0f);
                outIdx++;

                t += step;
            }
            generatedFrames = outIdx;

            _resampleT = t - inputFrames;
            int lastIdx = 2 + inputFrames;
            _historyL[0] = _workBufferL[lastIdx - 2];
            _historyL[1] = _workBufferL[lastIdx - 1];
            _historyL[2] = _workBufferL[lastIdx];
            _historyR[0] = _workBufferR[lastIdx - 2];
            _historyR[1] = _workBufferR[lastIdx - 1];
            _historyR[2] = _workBufferR[lastIdx];
        }

        if (generatedFrames <= 0) return;

        // --- 極小時間のロック: 生成されたサンプルをリングバッファへコピー ---
        lock (_bufferLock)
        {
            for (int i = 0; i < generatedFrames; i++)
            {
                _ringBuffer[_writePos] = _resampledOutL[i];
                _ringBuffer[_writePos + 1] = _resampledOutR[i];
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

            // バッファ蓄積の許容幅を 60ms に広げ、他アプリ起動時の瞬間的なジッター（30〜40ms程度）での強制スキップを防止。
            // 60ms を超えて滞留した場合のみ目標値（25ms）へスキップし、その際も先頭16サンプルをクロスフェードして段差（プツッ音）を除去
            int maxLatencyFrames = (int)(_outputSampleRate * 0.060);
            int targetLatencyFrames = (int)(_outputSampleRate * 0.025);
            if (_availableFrames > maxLatencyFrames)
            {
                int skip = _availableFrames - targetLatencyFrames;
                int prevPos = (_readPos - 2 + _ringCapacity) % _ringCapacity;
                float prevL = _ringBuffer[prevPos];
                float prevR = _ringBuffer[prevPos + 1];

                _readPos = (_readPos + skip * 2) % _ringCapacity;
                _availableFrames = targetLatencyFrames;

                int blendFrames = Math.Min(16, _availableFrames);
                int blendPos = _readPos;
                for (int b = 0; b < blendFrames; b++)
                {
                    float ramp = (b + 1) / (float)blendFrames;
                    _ringBuffer[blendPos] = prevL * (1f - ramp) + _ringBuffer[blendPos] * ramp;
                    _ringBuffer[blendPos + 1] = prevR * (1f - ramp) + _ringBuffer[blendPos + 1] * ramp;
                    blendPos = (blendPos + 2) % _ringCapacity;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static float InterpolateHermite(float y_prev, float y0, float y1, float y2, float t)
    {
        float c0 = y0;
        float c1 = 0.5f * (y1 - y_prev);
        float c2 = y_prev - 2.5f * y0 + 2.0f * y1 - 0.5f * y2;
        float c3 = 0.5f * (y2 - y_prev) + 1.5f * (y0 - y1);
        return ((c3 * t + c2) * t + c1) * t + c0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void WriteSampleToBuffer(byte[] buffer, int outByteIdx, float left, float right, int bytesPerSample)
    {
        if (_outputIsFloat && bytesPerSample == 4)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(outByteIdx), left);
            if (_outputChannels > 1)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(outByteIdx + 4), right);
            }
            for (int c = 2; c < _outputChannels; c++)
            {
                Array.Clear(buffer, outByteIdx + c * 4, 4);
            }
        }
        else if (bytesPerSample == 2)
        {
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
        else if (bytesPerSample == 3)
        {
            int iLeft = Math.Clamp((int)(left * 8388607f), -8388608, 8388607);
            int iRight = Math.Clamp((int)(right * 8388607f), -8388608, 8388607);

            buffer[outByteIdx] = (byte)(iLeft & 0xFF);
            buffer[outByteIdx + 1] = (byte)((iLeft >> 8) & 0xFF);
            buffer[outByteIdx + 2] = (byte)((iLeft >> 16) & 0xFF);

            if (_outputChannels > 1)
            {
                buffer[outByteIdx + 3] = (byte)(iRight & 0xFF);
                buffer[outByteIdx + 4] = (byte)((iRight >> 8) & 0xFF);
                buffer[outByteIdx + 5] = (byte)((iRight >> 16) & 0xFF);
            }
            for (int c = 2; c < _outputChannels; c++)
            {
                Array.Clear(buffer, outByteIdx + c * 3, 3);
            }
        }
        else
        {
            int frameSize = bytesPerSample * _outputChannels;
            Array.Clear(buffer, outByteIdx, frameSize);
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

            // アンダーラン直後からの復帰時は 16 サンプルでフェードインしてクリック音を防止
            bool needFadeIn = _wasUnderflow && framesToRead > 0;
            int fadeInFrames = Math.Min(16, framesToRead);

            for (int f = 0; f < framesToRead; f++)
            {
                float left = _ringBuffer[_readPos] * vol;
                float right = _ringBuffer[_readPos + 1] * vol;
                _readPos = (_readPos + 2) % _ringCapacity;

                if (_noiseGateEnabled)
                {
                    float maxAmp = Math.Max(Math.Abs(left), Math.Abs(right));
                    bool isOpen = _gateCurrentGain > 0.0001f || _gateHoldCounter > 0;
                    float activeThreshold = isOpen ? _gateCloseThreshold : _gateOpenThreshold;

                    if (maxAmp >= activeThreshold)
                    {
                        _gateHoldCounter = _holdFrames;
                        _gateCurrentGain = Math.Min(1.0f, _gateCurrentGain + _attackStep);
                    }
                    else if (_gateHoldCounter > 0)
                    {
                        _gateHoldCounter--;
                        if (_gateCurrentGain < 1.0f)
                        {
                            _gateCurrentGain = Math.Min(1.0f, _gateCurrentGain + _attackStep);
                        }
                    }
                    else
                    {
                        _gateCurrentGain = Math.Max(0.0f, _gateCurrentGain - _releaseStep);
                    }

                    left *= _gateCurrentGain;
                    right *= _gateCurrentGain;
                }

                if (needFadeIn && f < fadeInFrames)
                {
                    float ramp = (f + 1) / (float)fadeInFrames;
                    left *= ramp;
                    right *= ramp;
                }

                WriteSampleToBuffer(buffer, outByteIdx, left, right, bytesPerSample);
                outByteIdx += frameSize;

                _lastOutL = left;
                _lastOutR = right;
            }
            _availableFrames -= framesToRead;

            // マイクデータ不足（アンダーラン）時:
            // 突然の 0 切断を避け、直前のサンプルから 0 へ 16 サンプルでスムーズにフェードアウト
            int remainingFrames = totalFramesRequested - framesToRead;
            if (remainingFrames > 0)
            {
                _wasUnderflow = true;
                int fadeOutFrames = Math.Min(16, remainingFrames);
                for (int f = 0; f < fadeOutFrames; f++)
                {
                    float ramp = (fadeOutFrames - 1 - f) / (float)fadeOutFrames;
                    float fadeL = _lastOutL * ramp;
                    float fadeR = _lastOutR * ramp;
                    WriteSampleToBuffer(buffer, outByteIdx, fadeL, fadeR, bytesPerSample);
                    outByteIdx += frameSize;
                }

                int clearFrames = remainingFrames - fadeOutFrames;
                if (clearFrames > 0)
                {
                    Array.Clear(buffer, outByteIdx, clearFrames * frameSize);
                }
                _lastOutL = 0f;
                _lastOutR = 0f;
            }
            else
            {
                _wasUnderflow = false;
            }
        }

        return count;
    }
}
