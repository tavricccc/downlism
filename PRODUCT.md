# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

The product itself is a Windows 11 desktop application. `website/` is its public website.

## Users

Inferred from the README: Windows users who download files, video, or torrents and want one place to track, pause, resume, and organize them. Chrome and Edge users can hand browser downloads to the app.

## Product Purpose

Downlism is a Windows download manager for HTTP files, video, and BitTorrent. It presents their transfers in one list, supports pause and resume, and preserves the list across restarts.

## Positioning

Three download engines share one queue, history, retry behavior, notifications, and native progress presentation. The HTTP engine uses segmented transfers and resumable ranges; video uses yt-dlp; BitTorrent uses an internal engine.

## Operating Context

Users paste links, use the Chrome or Edge extension, or add magnet links and `.torrent` files. They can choose a video or audio format before downloading, set destination and concurrency preferences, and leave transfers running in the system tray.

## Capabilities and Constraints

- Current README version: 0.10.2 preview.
- Windows 11 build 26100 or later, x64. User-scope installation without administrator permission.
- Latest installer is `Downlism.Setup.exe` at https://github.com/tavricccc/downlism/releases/latest.
- Installer is currently unsigned; SmartScreen may identify an unknown publisher.
- Chrome and Edge extension support is complete. Firefox support has not started.
- Video tools are obtained on first use when needed.
- The installer contains both shared-runtime and self-contained variants and selects based on the machine.

## Brand Commitments

The existing name is Downlism. The icon/mark and real app screenshots are in `docs/assets/`, `artifacts/`, and `website/assets/`. The existing site uses Traditional Chinese and links to the public GitHub repository and sibling Flowlism and Peeklism sites.

## Evidence on Hand

`README.md` documents current features, installation, and limitations. `artifacts/customization-smoke/` contains actual app captures. The older captures in `website/assets/` depict an older UI; do not present them as the current release.

## Product Principles

- Show transfer state clearly, including stalled or incomplete segments.
- Keep user control over timing, destination, and background behavior.
- State current support and installation caveats plainly.
- Use actual product behavior and screenshots as evidence on the website.
