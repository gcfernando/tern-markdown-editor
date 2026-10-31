# A synthetic test subprocess only. It loads the built application libraries; no build,
# profile, network, application state, or user document is involved.
param(
    [Parameter(Mandatory)][ValidateSet('save','recovery')][string]$Mode,
    [Parameter(Mandatory)][string]$Phase,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [Parameter(Mandatory)][string]$AssemblyDirectory,
    [Parameter(Mandatory)][guid]$SessionId
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Threading;
public static class PersistenceCrashSignal
{
    public static string ExpectedPhase;
    public static void Reached(string phase)
    {
        if (phase != ExpectedPhase) return;
        Console.WriteLine("TERN-PERSISTENCE-PHASE:" + phase);
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
}
'@
[PersistenceCrashSignal]::ExpectedPhase = $Phase
$callback = [Delegate]::CreateDelegate([Action[string]], [PersistenceCrashSignal].GetMethod('Reached'))
foreach ($name in @('Tern.Domain', 'Tern.Filesystem', 'Tern.Recovery')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $AssemblyDirectory ($name + '.dll')))
}
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
if ($Mode -eq 'save') {
    $path = Join-Path $FixtureRoot 'source.md'
    $service = [Tern.Filesystem.DocumentFileService]::new()
    $loaded = $service.LoadAsync($path, $null, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $service.GetType().GetProperty('SavePhase', $flags).SetValue($service, $callback)
    [void]$service.SaveAsync($path, "new saved revision`n", $loaded.Format, $loaded.Baseline, $false, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
} else {
    $store = [Tern.Recovery.RecoveryStore]::new($FixtureRoot)
    $store.GetType().GetProperty('CheckpointPhase', $flags).SetValue($store, $callback)
    $record = [Tern.Recovery.RecoveryRecord]::new($SessionId, $null, 'crash-fixture.md', 3, [DateTime]::UtcNow, $null,
        [Tern.Domain.Text.TextFileFormat]::NewFileDefault, 'new durable checkpoint')
    $store.WriteCheckpointAsync($record, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
throw 'The selected persistence phase was not reached.'
