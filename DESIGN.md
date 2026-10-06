---
name: Precision Industrial Interface
colors:
  surface: '#121416'
  surface-dim: '#121416'
  surface-bright: '#37393b'
  surface-container-lowest: '#0c0e10'
  surface-container-low: '#1a1c1e'
  surface-container: '#1e2022'
  surface-container-high: '#282a2c'
  surface-container-highest: '#333537'
  on-surface: '#e2e2e5'
  on-surface-variant: '#c2c6d4'
  inverse-surface: '#e2e2e5'
  inverse-on-surface: '#2f3133'
  outline: '#8c919d'
  outline-variant: '#424752'
  surface-tint: '#a8c8ff'
  primary: '#a8c8ff'
  on-primary: '#003062'
  primary-container: '#005fb8'
  on-primary-container: '#cadcff'
  inverse-primary: '#005db5'
  secondary: '#71dab1'
  on-secondary: '#003827'
  secondary-container: '#34a27d'
  on-secondary-container: '#003122'
  tertiary: '#ffb3ac'
  on-tertiary: '#680008'
  tertiary-container: '#bc1c21'
  on-tertiary-container: '#ffd0cb'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#d6e3ff'
  primary-fixed-dim: '#a8c8ff'
  on-primary-fixed: '#001b3d'
  on-primary-fixed-variant: '#00468b'
  secondary-fixed: '#8df7cc'
  secondary-fixed-dim: '#71dab1'
  on-secondary-fixed: '#002116'
  on-secondary-fixed-variant: '#00513b'
  tertiary-fixed: '#ffdad6'
  tertiary-fixed-dim: '#ffb3ac'
  on-tertiary-fixed: '#410003'
  on-tertiary-fixed-variant: '#930010'
  background: '#121416'
  on-background: '#e2e2e5'
  surface-variant: '#333537'
typography:
  display-lg:
    fontFamily: Inter
    fontSize: 32px
    fontWeight: '700'
    lineHeight: 40px
  headline-md:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
  data-lg:
    fontFamily: JetBrains Mono
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
  body-md:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 24px
  label-sm:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '600'
    lineHeight: 16px
    letterSpacing: 0.05em
  mono-label:
    fontFamily: JetBrains Mono
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  unit: 4px
  xs: 4px
  sm: 8px
  md: 16px
  lg: 24px
  xl: 32px
  gutter: 16px
  margin: 24px
---

## Brand & Style

The design system is engineered for the high-stakes environment of wet leak testing, where clarity and rapid error recognition are critical. The brand personality is clinical, reliable, and utilitarian, prioritizing functional performance over aesthetic flourish. 

The visual style is **Minimalist Industrial**. It utilizes a flat, high-contrast architecture inspired by modern instrumentation and professional diagnostic software. By eliminating gradients, blurs, and organic shadows, the UI ensures maximum legibility under harsh factory lighting. The interface should feel like a high-precision tool—stable, responsive, and authoritative.

## Colors

The palette is anchored in a deep charcoal environment to reduce eye strain during long shifts. 

- **Primary (Industrial Blue):** Used for interactive elements, primary actions, and active selection states.
- **Status Colors:** These are the most critical semantic tokens. Green (#2D9D78) denotes a "Pass" or "Running" state; Red (#D32F2F) indicates "Fail" or "Emergency Stop"; Amber (#F59E0B) is reserved for manual overrides or maintenance warnings.
- **Surface:** A lighter grey (#2C2E33) creates a clear distinction for data containers and input fields against the deep background.

## Typography

This design system uses a dual-font approach to maximize readability in an industrial context. 

- **Inter:** Used for all UI labels, navigation, and instructional text. Its high x-height and neutral character ensure legibility at varying distances.
- **JetBrains Mono:** Employed exclusively for numeric data and pressure readings. Monospaced characters prevent "jitter" when values update rapidly, allowing operators to track fluctuations with precision.

All labels should be rendered in high-contrast white (#FFFFFF) or light grey (#E4E7EB). Use `label-sm` in uppercase for category headers to establish a clear information hierarchy.

## Layout & Spacing

The layout follows a **Fixed Grid** model to ensure that critical controls (like "Stop" or "Station Start") remain in consistent, predictable locations across different screens. 

- **Grid:** A 12-column grid with 16px gutters.
- **Rhythm:** All spacing is based on a 4px baseline. Use 16px (md) for internal padding of data blocks and 8px (sm) for spacing between related input fields.
- **Safe Areas:** Large touch targets are prioritized. No interactive element should be smaller than 44x44px to accommodate gloved operation.

## Elevation & Depth

This design system rejects shadows and physical depth in favor of **Tonal Layers** and **Bold Outlines**.

- **Level 0 (Background):** Deep charcoal (#1A1C1E). Used for the "floor" of the application.
- **Level 1 (Surface):** Dark grey (#2C2E33). Used for grouping related data or controls into "blocks".
- **Level 2 (Interactive):** Elements that can be clicked or toggled use a 1px solid border (#4A4D54) to define their hit area. 
- **Focus/Active:** When an element is focused or station operation is active, use a 2px solid primary blue border.

This flat hierarchy ensures that the user's focus remains on color-coded status indicators rather than simulated lighting effects.

## Shapes

The shape language is **Soft (0.25rem)**. 

While the system is largely clinical, slight rounding on buttons and data containers prevents the UI from feeling overly aggressive and helps distinguish individual components in a dense information environment. 

- **Containers:** 4px radius.
- **Buttons:** 4px radius.
- **Status Badges:** 2px radius (near-sharp) to maintain a technical, "tag-like" appearance.

## Components

### Buttons
- **Primary:** Solid Industrial Blue background with White text.
- **Secondary:** Transparent background with a 1px Grey border.
- **Critical (Stop/Reset):** Solid Status Red background. Only used for emergency or fault-clearing actions.

### Data Blocks (Compact)
Containers for leak test metrics. Should feature a `label-sm` top-aligned and a `data-lg` (monospaced) value centered. Use the `surface` color for the background to group related metrics (e.g., Pressure, Flow Rate, Duration).

### Status Badges
Small, high-visibility tags. They must use the semantic status colors (Green, Red, Amber) as the background with high-contrast text. No icons are required if the text label (e.g., "PASS", "FAIL") is clear.

### Input Fields
Darker than the surface color (#1A1C1E) with a 1px border. The cursor and text should be the primary blue or white.

### Lists/Logs
For "Event Logs" or "Test History," use zebra-striping (alternating between Background and Surface colors) for row distinction without the need for horizontal dividers.
