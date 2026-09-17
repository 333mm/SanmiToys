using System;
using System.IO;
using System.Text;
using SanmiToys.Modules.OmniGlance.Core;
using Xunit;

namespace SanmiToys.Tests;

/// <summary>
/// OmniGlanceSoundPlayer の2連ビープ音（ピピッ）生成および再生テスト
/// </summary>
public class OmniGlanceSoundPlayerTests
{
    [Fact]
    public void GenerateDoubleBeepWav_ProducesValidRiffWaveHeader()
    {
        // 1046Hz, 45ms beep, 35ms gap, 0.20 volume
        byte[] wav = OmniGlanceSoundPlayer.GenerateDoubleBeepWav(1046, 45, 35, 0.20);

        Assert.NotNull(wav);
        Assert.True(wav.Length > 44, "WAV data must be larger than the 44-byte standard header");

        using var ms = new MemoryStream(wav);
        using var br = new BinaryReader(ms);

        // RIFF header
        string riff = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("RIFF", riff);

        int fileSizeMinus8 = br.ReadInt32();
        Assert.Equal(wav.Length - 8, fileSizeMinus8);

        string wave = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("WAVE", wave);

        // fmt chunk
        string fmt = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("fmt ", fmt);

        int fmtChunkSize = br.ReadInt32();
        Assert.Equal(16, fmtChunkSize);

        short audioFormat = br.ReadInt16();
        Assert.Equal(1, audioFormat); // PCM

        short numChannels = br.ReadInt16();
        Assert.Equal(1, numChannels); // Mono

        int sampleRate = br.ReadInt32();
        Assert.Equal(44100, sampleRate);

        int byteRate = br.ReadInt32();
        Assert.Equal(44100 * 2, byteRate);

        short blockAlign = br.ReadInt16();
        Assert.Equal(2, blockAlign);

        short bitsPerSample = br.ReadInt16();
        Assert.Equal(16, bitsPerSample);

        // data chunk
        string dataHeader = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("data", dataHeader);

        int dataSize = br.ReadInt32();
        Assert.Equal(wav.Length - 44, dataSize);
    }

    [Fact]
    public void GenerateDoubleBeepWav_ContainsDistinctBeepsAndSilentGap()
    {
        const int sampleRate = 44100;
        int beepMs = 45;
        int gapMs = 35;
        int beepSamples = (sampleRate * beepMs) / 1000;
        int gapSamples = (sampleRate * gapMs) / 1000;
        int totalExpectedSamples = (beepSamples * 2) + gapSamples;

        byte[] wav = OmniGlanceSoundPlayer.GenerateDoubleBeepWav(1046, beepMs, gapMs, 0.20);

        int totalBytes = 44 + (totalExpectedSamples * 2);
        Assert.Equal(totalBytes, wav.Length);

        // Read audio samples (16-bit signed PCM)
        short[] samples = new short[totalExpectedSamples];
        for (int i = 0; i < totalExpectedSamples; i++)
        {
            samples[i] = BitConverter.ToInt16(wav, 44 + (i * 2));
        }

        // 1. Verify Beep 1 has audible energy (non-zero)
        bool beep1HasSound = false;
        for (int i = 0; i < beepSamples; i++)
        {
            if (Math.Abs(samples[i]) > 100)
            {
                beep1HasSound = true;
                break;
            }
        }
        Assert.True(beep1HasSound, "Beep 1 segment must have audible signal");

        // 2. Verify Gap is completely silent (all 0s)
        int gapStart = beepSamples;
        for (int i = 0; i < gapSamples; i++)
        {
            Assert.Equal(0, samples[gapStart + i]);
        }

        // 3. Verify Beep 2 has audible energy (non-zero)
        int beep2Start = beepSamples + gapSamples;
        bool beep2HasSound = false;
        for (int i = 0; i < beepSamples; i++)
        {
            if (Math.Abs(samples[beep2Start + i]) > 100)
            {
                beep2HasSound = true;
                break;
            }
        }
        Assert.True(beep2HasSound, "Beep 2 segment must have audible signal");
    }

    [Fact]
    public void PlayAlertSound_ExecutesWithoutException()
    {
        // winmm PlaySound (SND_ASYNC) should never throw
        var exception = Record.Exception(() => OmniGlanceSoundPlayer.PlayAlertSound());
        Assert.Null(exception);
    }
}
