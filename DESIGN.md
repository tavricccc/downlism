---
name: Downlism
description: Visual system for the public Downlism website
colors:
  primary: "#075bb8"
  primary-hover: "#004a9b"
  page: "#f5f7f8"
  surface: "#ffffff"
  soft: "#e9f0f6"
  ink: "#142331"
  muted: "#4d5d6a"
  line: "#c9d4dd"
  dark-page: "#111920"
  dark-surface: "#19232c"
  dark-ink: "#f1f5f7"
typography:
  display:
    fontFamily: "Downlism Sans, Microsoft JhengHei UI, system-ui, sans-serif"
    fontSize: "clamp(3.3rem, 5.1vw, 5.6rem)"
    fontWeight: 800
    lineHeight: 1.2
    letterSpacing: "-.035em"
  headline:
    fontFamily: "Downlism Sans, Microsoft JhengHei UI, system-ui, sans-serif"
    fontSize: "clamp(2.55rem, 3.8vw, 4.2rem)"
    fontWeight: 800
    lineHeight: 1.2
    letterSpacing: "-.035em"
  body:
    fontFamily: "Downlism Sans, Microsoft JhengHei UI, system-ui, sans-serif"
    fontSize: "16px"
    lineHeight: 1.68
rounded:
  control: "4px"
spacing:
  shell-gutter: "48px"
  mobile-gutter: "18px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.surface}"
    rounded: "{rounded.control}"
    padding: "12px 22px"
  button-primary-hover:
    backgroundColor: "{colors.primary-hover}"
    textColor: "{colors.surface}"
    rounded: "{rounded.control}"
    padding: "12px 22px"
---

# Design System: Downlism

## Overview

**Creative North Star: A clear transfer record**

The site gives a precise account of what the desktop app does. Large Traditional Chinese type introduces each task. Real app captures and a labeled range diagram carry the proof. The page has a steady reading rhythm and a single cobalt accent.

## Colors

Use the cool neutral page and surface tokens throughout. Cobalt marks actions, transfer ranges, and selected words. Dark mode swaps the page and text tokens through the system preference while keeping the same hierarchy.

## Typography

The site self-hosts a subset of Noto Sans TC as Downlism Sans. Display and section headlines use weight 800 with tight tracking. Body text stays at 16px or larger; small captions and metadata use at least 14px. Monospace is limited to protocol labels and code.

## Layout

The container is at most 1380px wide. Desktop hero and feature sections use unequal two-column grids; the engine list uses horizontal rows. Below 760px these become one column and retain 18px side gutters. The hero uses viewport-aware minimum height only on desktop.

## Elevation & Depth

Lines and tonal backgrounds define sections. Product screenshots use a thin frame. The mobile navigation panel alone has a soft neutral shadow to place it above the document.

## Shapes

The page uses square panels and screenshots. Primary buttons have a 4px radius. The range diagram uses rectangular bars, echoing byte ranges without imitating the app UI.

## Components

### Buttons

Primary links use cobalt fill, a compact rectangular shape, and an offset focus ring. Hover deepens the fill; active press moves by one pixel. Text links are cobalt and underlined.

### Navigation

Desktop navigation sits on one line. At tablet widths, a native details control exposes the same anchors. Links close that control after selection.

### Product evidence

App captures retain their native window chrome and carry captions identifying them as real screens. The range diagram is labeled as an illustration.

## Do's and Don'ts

- **Do** ground claims in the current README and current app captures.
- **Do** keep the release link pointed at GitHub's latest release.
- **Do** keep the single accent and consistent light or dark theme across the page.
- **Don't** revive the old site's claim that the current app shows a segmented progress bar.
- **Don't** substitute styled HTML for actual app screenshots.
