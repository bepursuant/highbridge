// Where the demo sends someone who does not have highbridge installed.
//
// GitHub serves the newest release's asset at a stable address. Upload the MSI from
// scripts/build-msi.ps1 under the fixed name highbridge-x86.msi (the versioned filename can
// change; this one should not). A private repository only lets people who are signed in to
// GitHub and allowed to read the repo download it.
//
// Change the host if the repository lives on GitHub Enterprise Server rather than github.com.
export const installerUrl = 'https://github.com/bepursuant/highbridge/releases/latest/download/highbridge-x86.msi';
