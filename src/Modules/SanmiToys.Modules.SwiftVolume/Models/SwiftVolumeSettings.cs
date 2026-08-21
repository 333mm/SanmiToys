namespace SanmiToys.Modules.SwiftVolume.Models;

public class SwiftVolumeSettings
{
    public bool IsEnabled { get; set; } = false;
    public bool ShowHud { get; set; } = true;
    public double HudDurationSeconds { get; set; } = 1.2;
    public int HudPosition { get; set; } = 0; // 0:中央, 1:上部, 2:下部, 3:左上, 4:右上, 5:左下, 6:右下
    public int HudSize { get; set; } = 1;     // 0:小, 1:中, 2:大
    public bool ShowDeviceSwitchHud { get; set; } = true;
    public bool OpenAtCursor { get; set; } = true;
    public bool MiddleClickMuteAll { get; set; } = true;

    // ホットキー設定（キーボード機能のみ）
    public bool HotkeyOpenMixerEnabled { get; set; } = true;
    public string HotkeyOpenMixer { get; set; } = "V";
    public bool HotkeyOpenMixerCtrl { get; set; } = true;
    public bool HotkeyOpenMixerAlt { get; set; } = false;
    public bool HotkeyOpenMixerShift { get; set; } = true;
    public bool HotkeyOpenMixerWin { get; set; } = false;

    public bool HotkeyMuteEnabled { get; set; } = false;
    public string HotkeyMute { get; set; } = "None";
    public bool HotkeyMuteCtrl { get; set; } = false;
    public bool HotkeyMuteAlt { get; set; } = false;
    public bool HotkeyMuteShift { get; set; } = false;
    public bool HotkeyMuteWin { get; set; } = false;

    public bool HotkeyMicMuteEnabled { get; set; } = false;
    public string HotkeyMicMute { get; set; } = "None";
    public bool HotkeyMicMuteCtrl { get; set; } = false;
    public bool HotkeyMicMuteAlt { get; set; } = false;
    public bool HotkeyMicMuteShift { get; set; } = false;
    public bool HotkeyMicMuteWin { get; set; } = false;
}
