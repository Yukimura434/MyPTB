# Printer configuration

PC 3.0.8 and Android Lite 0.1.9 expose the paper settings reported by the selected printer. Keep the loaded paper consistent with the selection; discovery and successful submission do not certify the physical output.

## Windows

Discovery uses the cached Windows level-4 printer list, without loading every printer's driver capabilities. Only the selected printer loads paper sizes, sources, resolutions and media types. The application stops waiting after 12 seconds; an in-flight driver call is shared, and a completed capability result can be reused for two minutes. A slow native driver call cannot be forcibly cancelled, but it no longer holds the whole discovery list.

Existing profile identities and the legacy `PaperType` source/tray field are retained. `MediaType` is a separate additive nullable SQLite column, copied to print jobs and audit settings. Null/default preserves the driver media setting. Explicit media types are validated through Windows PrintTicket capabilities, converted to DEVMODE, then applied to the existing PrintDocument. Unsupported saved media fails explicitly instead of silently switching to plain paper. Drivers without PrintTicket media capabilities retain their default and existing paper/source/resolution controls.

## Android

IPP capabilities supply supported paper sizes, media types and print quality. Range endpoints are excluded from selectable paper sizes. Default choices do not emit overrides. Explicit selections persist with the chosen printer and are validated again before submission. Media type uses an IPP media-col collection; a selected paper size accompanies it as media-size in hundredths of a millimetre. Quality is a supported IPP enum. Existing one-sided, portrait and fill behavior remains.

The test action always sends one synthetic page. A printer that exposes only one quality offers only that quality plus its default; the app does not invent an unsupported high-quality mode.

## Validation

`Scripts/Validation/Run-PrinterCapabilitiesCheck.ps1` checks discovery, Brother media conversion, additive persistence and native PC layouts. Physical submission is opt-in with `-Print`. Android's `X111Instrumentation` uses isolated preferences and the production selectors/client; its optional print flag must only be used with explicit hardware-print authorization. Unit tests cover unsupported choices and the IPP size/type/quality wire encoding. Canon and Brother qualification does not certify physical Epson output.
