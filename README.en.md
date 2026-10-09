# NOVA VPN

[Русский](README.md) | **English**

[![Release](https://img.shields.io/github/v/release/leoraijin/NOVA-VPN?label=release&color=7863C5)](https://github.com/leoraijin/NOVA-VPN/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/leoraijin/NOVA-VPN/total?color=7863C5)](https://github.com/leoraijin/NOVA-VPN/releases)
![C#](https://img.shields.io/badge/C%23-.NET%20Framework%204.8.1-512BD4?logo=dotnet)
![UI](https://img.shields.io/badge/UI-WPF-7863C5)

A **C# + WPF** VPN client for Windows powered by [sing-box](https://github.com/SagerNet/sing-box). Import your server configuration, choose a traffic mode, and configure routes for websites and applications.

**[Download the latest installer](https://github.com/leoraijin/NOVA-VPN/releases/latest)** · [Release notes](https://github.com/leoraijin/NOVA-VPN/releases) · [Support](SUPPORT.md)

<p align="center">
  <img src="docs/images/windows-light.png" alt="NOVA VPN Windows Light home screen" width="900">
</p>

Screenshots use ephemeral demo profiles in version 2.3.16. No VPN connection was started.

## Features

- Profiles and subscriptions, server selection and switching.
- Website and application routing; website rules take precedence over browser rules.
- VPN, VPN + Zapret and Zapret modes.
- Tunnel, DNS and connectivity diagnostics.
- Settings protected with Windows DPAPI under the current user account.
- Liquid Glass, One UI, Windows Light, Windows Dark and Amber Glass themes.
- GitHub update checks and release summaries.

## Platforms

| Platform | Distribution |
| --- | --- |
| Windows | [Installers in Releases](https://github.com/leoraijin/NOVA-VPN/releases/latest) |
| Android | [Separate Android project](https://github.com/leoraijin/NOVA-VPN-Android) |

This repository contains the **Windows client source code**, documentation, and releases with installers. GitHub's “Source code” archives contain the source; download the ready-to-run installer separately from the latest release's Assets.

## Build from source

The client is written in C# and WPF for .NET Framework 4.8.1. Open `NOVA VPN.csproj` in Visual Studio 2022 with the .NET desktop development workload, or build it with MSBuild. Building the complete installer additionally requires external sing-box and Zapret payloads; their licenses and notices are listed in [THIRD_PARTY.md](THIRD_PARTY.md). VPN profiles and user keys are not included in this repository.

## Getting started

1. Download the `.exe` installer from the latest release's **Assets**.
2. Install the application. Windows may request administrative permissions.
3. Add your profile or subscription in **Серверы** (Servers).
4. Select a server and mode, then press **Подключить** (Connect).
5. Configure rules in **Маршрутизация** (Routing).

The installer contains no user profiles, subscription links or VPN keys.

## Visual styles

| Liquid Glass | Windows Dark |
| --- | --- |
| ![Liquid Glass](docs/images/liquid-glass.png) | ![Windows Dark](docs/images/windows-dark.png) |

The [documentation index](docs/README.md) links to installation, routing, design and status notes in Russian. See [STATUS](docs/STATUS.md) for actual checks; the current frame probe has not confirmed 120 FPS.

Theme names describe NOVA's styles and do not imply affiliation with Apple, Samsung or Microsoft.

## Licensing

NOVA VPN's own code is licensed under the **GNU General Public License version 3 (GPL-3.0-only)**; see [`LICENSE`](LICENSE). Third-party components and data retain their respective licenses; see [THIRD_PARTY.md](THIRD_PARTY.md).
