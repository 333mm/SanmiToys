# SanmiToys

> **Next-Generation Windows Desktop Utility Suite**  
> A modular productivity suite for power users on Windows.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![UI Fluent](https://img.shields.io/badge/UI-WPF--UI%20Fluent-0078D4?logo=windows)](https://wpfui.lepo.co/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D7)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

---

## 🚀 Modules

### 🖐️ **FluidDrag**
Intuitively move windows simply by clicking and dragging empty background areas—no need to aim for the title bar.
- Automatic exclusion for full-screen apps and maximized windows
- Flexible exclusion rules by process name or window title

### 💡 **FocusDimmer**
Seamlessly dims non-active windows to eliminate distractions and maximize your focus.
- Multi-monitor support (synchronized or per-display settings)
- Modern color palette dimming & automatic idle dimming
- Built-in Window Inspector for one-click exclusion and whitelist management

### 🔍 **SnapTrans**
Snipping-tool-style area selection with instant OCR text recognition, real-time translation, and Text-to-Speech (TTS).
- Supports multiple translation engines: Google, DeepL, Gemini, and OpenAI
- Automatic clipboard copy for both source and translated text

### 🔊 **SwiftVolume**
Independent taskbar tray icon, modern flyout volume mixer, mouse wheel adjustments, and HUD overlays.
- Per-device volume mixer controls
- Mouse wheel volume scrolling with a sleek HUD display

---

## 🌐 Supported Languages

SanmiToys is fully localized in:
- 🇯🇵 日本語 (Japanese)
- 🇺🇸 English
- 🇨🇳 简体中文 (Simplified Chinese)
- 🇹🇼 繁體中文 (Traditional Chinese)
- 🇰🇷 한국어 (Korean)

---

## ☕ Support the Developer

If you find SanmiToys helpful, consider supporting ongoing development and new features!

- **🇯🇵 Japan**: [Support via OFUSE](https://ofuse.me/d3a3316d)
- **🌍 Global**: [Buy Me a Coffee ☕](https://buymeacoffee.com/sanmi)

---

## 🛠️ Build & Run

```powershell
# Clone the repository
git clone [https://github.com/333mm/SanmiToys.git](https://github.com/333mm/SanmiToys.git)

# Build solution
dotnet build SanmiToys.sln

# Run Host application
dotnet run --project src/SanmiToys.Host/SanmiToys.Host.csproj
