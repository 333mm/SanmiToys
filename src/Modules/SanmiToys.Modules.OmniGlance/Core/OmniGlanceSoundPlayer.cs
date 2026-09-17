using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SanmiToys.Modules.OmniGlance.Core;

/// <summary>
/// OmniGlanceの警告通知用効果音プレイヤー (winmm PlaySound 使用)
/// マイクミュート音との混同を防ぎ、明瞭で歯切れの良い2連ビープ音（ピピッ）をインメモリWAV合成で再生
/// </summary>
public static class OmniGlanceSoundPlayer
{
    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] ptrToSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    // 警告用2連ビープ音「ピピッ」 (C6 1046Hz, 各45ms, 無音35ms, 音量 0.20)
    private static readonly byte[] _alertBeepWav = GenerateDoubleBeepWav(1046, 45, 35, 0.20);

    private static long _lastPlayTicks = 0;

    /// <summary>
    /// 警告表示時の効果音（2連ビープ音）を再生
    /// </summary>
    public static void PlayAlertSound()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastPlayTicks);
        if (now - last < 200) return; // 200ms以内の連打・二重発音を抑制
        Interlocked.Exchange(ref _lastPlayTicks, now);

        try
        {
            PlaySound(_alertBeepWav, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        }
        catch { }
    }

    /// <summary>
    /// 2連ビープ（ピピッ）のWAVバイト配列を生成 (Hann Window による滑らかなエンベロープでクリックノイズ完全防止)
    /// </summary>
    /// <param name="freq">ビープ音の周波数 (Hz)</param>
    /// <param name="beepDurationMs">各ビープ音の再生時間 (ミリ秒)</param>
    /// <param name="gapDurationMs">ビープ間の無音時間 (ミリ秒)</param>
    /// <param name="volume">音量 (0.0〜1.0)</param>
    /// <returns>生成されたWAV形式のバイト配列</returns>
    internal static byte[] GenerateDoubleBeepWav(int freq, int beepDurationMs, int gapDurationMs, double volume)
    {
        const int sampleRate = 44100;
        int beepSamples = (sampleRate * beepDurationMs) / 1000;
        int gapSamples = (sampleRate * gapDurationMs) / 1000;
        int totalSamples = (beepSamples * 2) + gapSamples;
        short[] samples = new short[totalSamples];

        // 1回目のビープ音
        for (int i = 0; i < beepSamples; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Sin(Math.PI * i / beepSamples);
            double val = Math.Sin(2 * Math.PI * freq * t) * env * volume;
            samples[i] = (short)(val * 32767);
        }

        // gapSamples 区間は配列初期値 0 のため完全な無音

        // 2回目のビープ音
        int beep2Start = beepSamples + gapSamples;
        for (int i = 0; i < beepSamples; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Sin(Math.PI * i / beepSamples);
            double val = Math.Sin(2 * Math.PI * freq * t) * env * volume;
            samples[beep2Start + i] = (short)(val * 32767);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(new char[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + totalSamples * 2);
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
