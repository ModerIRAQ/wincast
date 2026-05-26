# Changelog

All notable changes to WinCast are documented here.

## 0.4.0

- Added full Arabic language support and Right-to-Left (RTL) layout mirroring across all panels, settings, and list alignments.
- Localized greetings, dates (using Arabic culture), confirmation dialogs, and dynamic indexing/update statuses.
- Fixed search box focus and disabled state locks when changing interface languages.
- Resolved localization bugs on the dashboard app count card and the bottom status bar.
- Fixed missing icons for Google search fallbacks and system native commands.

## 0.3.0

- Added corrected and enhanced Calculator supporting modulo, exponents, negation, trigonometric and math functions, constants, and percentages.
- Added URL auto-detection to quickly open web addresses in the default browser.
- Added Web Search fallback option when local search matches are low.
- Added native system command support (sleep, lock, shutdown, restart, signout, screenoff, emptybin) with ContentDialog confirmation for destructive tasks.
- Added premium staggered fade-and-slide entry animations for search results.
- Refactored SearchEngine internals to a clean, extensible, interface-based plugin architecture.

## 0.2.0

- Added automatic release tag creation from the project version.
- Added footer update notification and one-click background update install flow.
- Improved release workflow authentication for automated tag pushes.

## 0.1.0

- Initial public release preparation.
- Added rounded command hub dashboard.
- Added visual theme options for Mica, Acrylic, Solid, light/dark/system mode, and surface strength.
- Added GitHub Releases update checking and installer download flow.
- Added Inno Setup installer packaging.
- Added tag-triggered GitHub Actions release workflow.
