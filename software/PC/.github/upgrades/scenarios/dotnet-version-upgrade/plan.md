# Aggiornamento a .NET 10 per UniSolder

## Understanding
L'utente ha richiesto l'aggiornamento della soluzione UniSolder.sln da .NET Framework 4.8 a .NET 10 (net10.0). L'analisi preliminare (assessment) ha rilevato che i progetti sono in stile legacy (non-SDK) e presentano possibili incompatibilità API.

## Assumptions
- Lavorerò nella cartella del repository indicata dalla soluzione fornita.
- User vuole target net10.0 (confermato).
- Se non specificato diversamente, creerò un branch di lavoro suggerito: upgrade-dotnet-10.
- Operazioni Git possono essere eseguite nella cartella E:\GitHub\UniSolder-5.2; se la repo è altrove, l'utente lo comunichi.

## Approach
Creerò un piano in più passi, dove ogni passo è atomico e ripetibile. Per ridurre il rischio, la migrazione seguirà questa sequenza: generare piano, convertire i progetti a SDK-style uno per uno, aggiornare il TFM (TargetFramework), aggiornare i pacchetti NuGet, applicare correzioni di compatibilità API, compilare e correggere errori, infine eseguire test e generare report. I cambiamenti saranno committati dopo ogni task (strategia: After Each Task).

## Key Files
- software/PC/UniSolder.sln - soluzione da aggiornare
- software/PC/SSComm/SSComm.csproj - esempio di progetto non-SDK
- software/PC/SSControl/SSControls.csproj
- software/PC/UniSolder/UniSolder.csproj
- software/PC/.github/upgrades/scenarios/dotnet-version-upgrade/assessment.md
- software/PC/.github/upgrades/scenarios/dotnet-version-upgrade/plan.md

## Risks & Open Questions
- Alcune dipendenze potrebbero non avere versioni compatibili con net10.0 (blocco parziale).
- Progetti che usano API Windows-only o System.* legacy potrebbero richiedere riscritture.
- Git repo locale non rilevato automaticamente in workspace corrente; confermare percorso repo per le operazioni Git.

## Steps
1. step-1: Scrivi plan.md — Generare e salvare il piano d'azione in plan.md
2. step-2: Prepara branch di lavoro — Creare/switch al branch upgrade-dotnet-10 e salvare eventuali modifiche locali (commit/stash)
3. step-3: Converti progetti a SDK-style — Per ogni progetto (.csproj) convertire il file in formato SDK (uno alla volta)
4. step-4: Aggiorna TargetFramework — Impostare <TargetFramework>net10.0</TargetFramework> nei csproj convertiti
5. step-5: Aggiorna pacchetti NuGet e sostituzioni API — Aggiornare pacchetti incompatibili e applicare alternative moderne
6. step-6: Compila e risolvi errori — Eseguire build completa, correggere breaking changes e warning
7. step-7: Test e chiusura — Eseguire test, aggiornare documentazione, scrivere progress-details.md e complete_task
