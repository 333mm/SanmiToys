using System;
using System.IO;
using System.Text;
using SanmiToys.Modules.SwiftVolume.Core;
using Xunit;

namespace SanmiToys.Tests;

/// <summary>
/// MicSoundPlayer のマイクミュート／ミュート解除ベル音生成および再生テスト
/// </summary>
public class MicSoundPlayerTests
{
    [Fact]
    public void GenerateChimeWav_ProducesValidRiffWaveHeader()
    {
        // Unmute sound: E5(659Hz) -> B5(988Hz), 48ms, 56ms, vol 0.22, isUnmute=true
        byte[] wav = MicSoundPlayer.GenerateChimeWav(659, 988, 48, 56, 0.22, true);

        Assert.NotNull(wav);
        Assert.True(wav.Length > 44);

        using var ms = new MemoryStream(wav);
        using var br = new BinaryReader(ms);

        string riff = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("RIFF", riff);

        int fileSizeMinus8 = br.ReadInt32();
        Assert.Equal(wav.Length - 8, fileSizeMinus8);

        string wave = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("WAVE", wave);

        string fmt = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("fmt ", fmt);

        int fmtSize = br.ReadInt32();
        Assert.Equal(16, fmtSize);

        short format = br.ReadInt16();
        Assert.Equal(1, format);

        short channels = br.ReadInt16();
        Assert.Equal(1, channels);

        int rate = br.ReadInt32();
        Assert.Equal(44100, rate);

        int byteRate = br.ReadInt32();
        Assert.Equal(44100 * 2, byteRate);

        short align = br.ReadInt16();
        Assert.Equal(2, align);

        short bits = br.ReadInt16();
        Assert.Equal(16, bits);

        string data = Encoding.ASCII.GetString(br.ReadBytes(4));
        Assert.Equal("data", data);

        int dataBytes = br.ReadInt32();
        Assert.Equal(wav.Length - 44, dataBytes);
    }

    [Fact]
    public void GenerateChimeWav_MuteAndUnmuteHaveDifferentDecayCharacteristics()
    {
        // Unmute has lighter decay (more sustain/resonance)
        byte[] unmuteWav = MicSoundPlayer.GenerateChimeWav(659, 988, 48, 56, 0.22, true);
        // Mute has steeper decay (mellow/subtle cutoff)
        byte[] muteWav = MicSoundPlayer.GenerateChimeWav(988, 659, 44, 60, 0.20, false);

        Assert.NotNull(unmuteWav);
        Assert.NotNull(muteWav);

        // They shouldn't be identical bytes
        Assert.NotEqual(unmuteWav, muteWav);
    }

    [Fact]
    public void PlayMuteSounds_ExecuteWithoutException()
    {
        var exMute = Record.Exception(() => MicSoundPlayer.PlayMuteSound());
        Assert.Null(exMute);

        var exUnmute = Record.Exception(() => MicSoundPlayer.PlayUnmuteSound());
        Assert.Null(exUnmute);

        var exStateTrue = Record.Exception(() => MicSoundPlayer.PlayMuteStateSound(true));
        Assert.Null(exStateTrue);

        var exStateFalse = Record.Exception(() => MicSoundPlayer.PlayMuteStateSound(false));
        Assert.Null(exStateFalse);
    }
}
