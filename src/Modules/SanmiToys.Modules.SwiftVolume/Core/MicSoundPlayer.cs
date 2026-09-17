using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SanmiToys.Modules.SwiftVolume.Core;

/// <summary>
/// マイクのミュート／ミュート解除時の確認通知音を再生する軽量サウンドプレイヤー (winmm PlaySound 使用)
/// 上品なベル調の完全5度トーン（倍音＋自然減衰）により、おしゃれで直感的な状態通知を提供
/// </summary>
public static class MicSoundPlayer
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] ptrToSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    // ミュート時: 落ち着いた安心感のある下降ベル音 (B5 988Hz -> E5 659Hz)
    private static readonly byte[] _muteWav = GenerateChimeWav(988, 659, 44, 60, 0.20, false);

    // ミュート解除時: 明るく開放的な上昇ベル音 (E5 659Hz -> B5 988Hz)
    private static readonly byte[] _unmuteWav = GenerateChimeWav(659, 988, 48, 56, 0.22, true);

    private static long _lastPlayTicks = 0;

    /// <summary>
    /// マイクミュート状態に応じた効果音を再生
    /// </summary>
    public static void PlayMuteStateSound(bool isMuted)
    {
        if (isMuted)
        {
            PlayMuteSound();
        }
        else
        {
            PlayUnmuteSound();
        }
    }

    /// <summary>
    /// ミュート時の下降音を再生
    /// </summary>
    public static void PlayMuteSound()
    {
        PlaySoundInternal(_muteWav);
    }

    /// <summary>
    /// ミュート解除時の上昇音を再生
    /// </summary>
    public static void PlayUnmuteSound()
    {
        PlaySoundInternal(_unmuteWav);
    }

    private static void PlaySoundInternal(byte[] wavBytes)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastPlayTicks);
        if (now - last < 100) return; // 100ms以内の連打・二重発音を抑制
        Interlocked.Exchange(ref _lastPlayTicks, now);

        try
        {
            PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        }
        catch { }
    }

    /// <summary>
    /// 上品なベル・チャイム調の2音WAVバイト配列を生成 (倍音付加・自然減衰・クロスフェード)
    /// </summary>
    /// <param name="freq1">1音目の周波数 (Hz)</param>
    /// <param name="freq2">2音目の周波数 (Hz)</param>
    /// <param name="ms1">1音目の再生時間 (ミリ秒)</param>
    /// <param name="ms2">2音目の再生時間 (ミリ秒)</param>
    /// <param name="volume">音量 (0.0〜1.0)</param>
    /// <param name="isUnmute">ミュート解除（開放的）かミュート（落ち着き）か</param>
    /// <returns>生成されたWAV形式のバイト配列</returns>
    internal static byte[] GenerateChimeWav(int freq1, int freq2, int ms1, int ms2, double volume, bool isUnmute)
    {
        const int sampleRate = 44100;
        int s1 = (sampleRate * ms1) / 1000;
        int s2 = (sampleRate * ms2) / 1000;
        int overlap = (sampleRate * 12) / 1000; // 12msの自然な音のクロスフェード
        int totalSamples = s1 + s2 - overlap;
        short[] samples = new short[totalSamples];

        // 1音目: 急速アタックと滑らかな減衰、第2倍音(12%)によるベルのような温かみ
        for (int i = 0; i < s1; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Sin(Math.PI * i / s1) * Math.Exp(-1.2 * i / s1);
            double val = (Math.Sin(2 * Math.PI * freq1 * t) + (0.12 * Math.Sin(4 * Math.PI * freq1 * t))) * env * volume;
            samples[i] = (short)(val * 32767);
        }

        // 2音目: ミュート解除は心地よい余韻、ミュートは耳障りにならないマイルドな減衰
        int start2 = s1 - overlap;
        double decayRate = isUnmute ? -1.0 : -2.2;
        for (int i = 0; i < s2; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Sin(Math.PI * i / s2) * Math.Exp(decayRate * i / s2);
            double val = (Math.Sin(2 * Math.PI * freq2 * t) + (0.12 * Math.Sin(4 * Math.PI * freq2 * t))) * env * volume;
            int idx = start2 + i;
            if (idx < totalSamples)
            {
                samples[idx] = (short)Math.Clamp(samples[idx] + (val * 32767), -32768, 32767);
            }
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(new char[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + (totalSamples * 2));
        bw.Write(new char[] { 'W', 'A', 'V', 'E' });
        bw.Write(new char[] { 'f', 'm', 't', ' ' });
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2); // BlockAlign
        bw.Write((short)16); // BitsPerSample
        bw.Write(new char[] { 'd', 'a', 't', 'a' });
        bw.Write(totalSamples * 2);

        for (int i = 0; i < totalSamples; i++)
        {
            bw.Write(samples[i]);
        }

        return ms.ToArray();
    }
}
