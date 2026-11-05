# Progress Details — Aggiornamento a .NET 10

## Cosa è stato fatto
- Convertiti i progetti SSComm, SSControl, UniSolder in SDK-style.
- Aggiornato TargetFramework a net10.0-windows per i progetti desktop.
- Rimosso using non supportato System.Runtime.Remoting.Messaging in software/PC/SSComm/usb_hid.cs.
- Ripristinati riferimenti assembly legacy per compatibilità locale (System.Data.DataSetExtensions, Microsoft.CSharp).
- Aggiunta temporanea NoWarn=WFO1000 nel progetto SSControl per bypassare errori del designer WinForms durante la migrazione.
- Eseguito dotnet restore (con --ignore-failed-sources) e dotnet build; compilazione completata con 229 avvisi.

## Aggiornamento 2026-10-04 (seconda sessione)
- LibUsbDotNet: rimosse le DLL vendorizzate (2.2.8) e aggiunto PackageReference LibUsbDotNet 2.2.85 (target net8.0, stessa API 2.x usata da usb_generic.cs). La 3.0.x è stata scartata: API completamente diversa e basata su libusb-1.0 nativa.
- Aggiunto software/PC/nuget.config (solo nuget.org) per rendere il restore indipendente da sorgenti NuGet locali della macchina.
- Build pulita (--no-incremental): 0 errori, 0 avvisi. Avvio dell'app verificato (finestra principale visibile).

## Aggiornamento 2026-10-04 (terza sessione)
- Crash OverflowException a 64 bit in usb_hid/devman.cs (IntPtr.ToInt32): rimosso l'intero livello P/Invoke HID (cartella SSComm/usb_hid) e riscritto SSComm/usb_hid.cs su HidSharp 2.6.4. API pubblica di USBHID invariata.
- Rimosso il trasporto LibUsbDotNet (SSComm/usb_generic.cs, mai usato dall'app) e il relativo pacchetto NuGet.
- Build: 0 errori, 0 avvisi.

## Problemi aperti
- Avvisi CA1416 e WFO1000: risolti (SupportedOSPlatform("windows") in PlatformCompat.cs, NoWarn rimosso da SSControls.csproj); la build non produce più avvisi.
- UniSolder.csproj contiene ancora impostazioni ClickOnce/bootstrapper per .NET Framework 4.5.2/3.5, non pertinenti per .NET 10 (innocue per la build).
- Comunicazione USB HID (HidSharp) da verificare con il dispositivo reale.

## Prossimi passaggi consigliati
1. Test con il dispositivo collegato (USB HID via HidSharp).
2. Eventuale pulizia delle impostazioni ClickOnce obsolete o passaggio a `dotnet publish`.
