using SanmiToys.Modules.OmniGlance.Core;
using SanmiToys.Modules.SwiftVolume.Core;
using Xunit;

namespace SanmiToys.Tests;

/// <summary>
/// OmniGlanceの警告音とSwiftVolumeのマイク効果音の差別化検証テスト
/// </summary>
public class SoundComparisonTests
{
    [Fact]
    public void OmniGlanceAndSwiftVolumeSounds_AreAcousticallyDistinct()
    {
        byte[] omniAlert = OmniGlanceSoundPlayer.GenerateDoubleBeepWav(1046, 45, 35, 0.20);
        byte[] micMute = MicSoundPlayer.GenerateChimeWav(988, 659, 44, 60, 0.20, false);
        byte[] micUnmute = MicSoundPlayer.GenerateChimeWav(659, 988, 48, 56, 0.22, true);

        // Different byte lengths due to different durations and structure
        Assert.NotEqual(omniAlert.Length, micMute.Length);
        Assert.NotEqual(omniAlert.Length, micUnmute.Length);

        // OmniGlance has a silent gap in the middle, whereas MicSoundPlayer has crossfaded continuous tones
        const int sampleRate = 44100;
        int beep1Samples = (sampleRate * 45) / 1000;
        int gapSamples = (sampleRate * 35) / 1000;

        // Verify OmniGlance has silence in its gap
        for (int i = 0; i < gapSamples; i++)
        {
            short omniSample = System.BitConverter.ToInt16(omniAlert, 44 + ((beep1Samples + i) * 2));
            Assert.Equal(0, omniSample);
        }

        // Verify Mic Mute has continuous sound at the transition (no silent gap)
        int micTransitionSample = (sampleRate * 44) / 1000;
        bool micHasEnergyAtTransition = false;
        for (int i = -50; i < 50; i++)
        {
            int idx = micTransitionSample + i;
            if (idx >= 0 && 44 + (idx * 2) + 1 < micMute.Length)
            {
                short micSample = System.BitConverter.ToInt16(micMute, 44 + (idx * 2));
                if (System.Math.Abs(micSample) > 50)
                {
                    micHasEnergyAtTransition = true;
                    break;
                }
            }
        }
        Assert.True(micHasEnergyAtTransition, "Mic mute sound should be a continuous legato transition, not a silent-gapped double beep");
    }
}
