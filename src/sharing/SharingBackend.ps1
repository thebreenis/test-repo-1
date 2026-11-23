param([Parameter(Mandatory=$true)][string]$RequestPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\backend\Core.ps1"
. "$PSScriptRoot\GuiBackend.ps1"
. "$PSScriptRoot\InstalledGames.ps1"
$script:BaseStoreCredential=$script:OriginalStoreCredential
function Get-SharingSources($Request){
 $script:InactiveSharingAccounts=@()
 $added=@($Request.accounts)
 $primary=& $script:BaseStoreCredential
 if(!$primary.AccountNameAvailable){throw 'Windows did not disclose the receipt account. Complete Windows sign-in before sharing.'}
 $sources=@([pscustomobject]@{id='';email=[string]$primary.AccountName})
 $primary.Token=$null
 foreach($row in $added){
  $id=[string]$row.id;$email=[string]$row.email
  if([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($email)){continue}
  if($email -ieq $sources[0].email){continue}
  try{
   $credential=Get-SelectedStoreCredential $id $email
   $credential.Token=$null
   if(!@($sources|Where-Object email -EQ $email).Count){$sources+=,[pscustomobject]@{id=$id;email=$email}}
  }catch{$script:InactiveSharingAccounts+=,$email;Send-GuiLog ('Added account '+$email+' needs sign-in again.') 'warning'}
 }
 return ,$sources
}
function Select-SharingSource($Source){
 $script:SelectedAccountId=[string]$Source.id
 $script:SelectedAccountEmail=[string]$Source.email
 if($script:SelectedAccountId){$script:OriginalStoreCredential={Get-SelectedStoreCredential $script:SelectedAccountId $script:SelectedAccountEmail}}
 else{$script:OriginalStoreCredential=$script:BaseStoreCredential}
 $script:StoreAccount=$script:SelectedAccountEmail
}
function Get-SourceProofs($Games,$Source){
 $proofs=@{};$times=@{}
 foreach($game in $Games){
  $folder=Join-Path $script:DataRoot ('Games\'+$game.package_family_name+'\Receipts')
  if(!(Test-Path -LiteralPath $folder)){continue}
  foreach($file in @(Get-ChildItem -LiteralPath $folder -Filter receipt-proof-private.json -File -Recurse|Sort-Object LastWriteTimeUtc -Descending)){
   try{
    $proof=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
    if($proof.origin -ne 'minted' -or $proof.storeAccount -ine $Source.email -or [int]$proof.gameId -ne [int]$game.index){continue}
    $path=Resolve-GuiSavedReceiptPath ([string]$proof.path)
    if(!$path.StartsWith(([IO.Path]::GetFullPath($folder).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){continue}
    if((Get-ResearchFileHash $path).Hash -ine $proof.sha256){continue}
    $script:TrustedReceiptHash=$null;$signed=Read-Receipt $path $game
    if(!$signed.SignatureVerified){continue}
    $proofs[[int]$game.index]=$signed;$times[[int]$game.index]=$file.LastWriteTimeUtc
    break
   }catch{continue}
  }
 }
 $batches=Join-Path $script:DataRoot 'Batches'
 if(Test-Path -LiteralPath $batches){
  foreach($file in @(Get-ChildItem -LiteralPath $batches -Filter bulk-summary-private.json -File -Recurse|Sort-Object LastWriteTimeUtc -Descending)){
   try{
    $summary=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
    if($summary.cancelled -or $summary.errors -gt 0 -or $summary.checked -ne $summary.total -or $summary.storeAccount -ine $Source.email){continue}
    foreach($entry in @($summary.entries|Where-Object status -eq 'unlicensed')){
     $id=[int]$entry.gameId
     if($proofs.ContainsKey($id) -and $times[$id] -le $file.LastWriteTimeUtc){$proofs.Remove($id)}
    }
    break
   }catch{continue}
  }
 }
 return $proofs
}
Initialize-GuiStorage
$request=Get-Content -LiteralPath $RequestPath -Raw|ConvertFrom-Json
$job=Split-Path ([IO.Path]::GetFullPath($RequestPath)) -Parent
$script:WorkerScratch=$job
$stream=[IO.File]::Open((Join-Path $job 'events-private.jsonl'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
$script:EventWriter=New-Object IO.StreamWriter($stream,(New-Object Text.UTF8Encoding($false)))
$script:EventWriter.AutoFlush=$true
$result=[ordered]@{success=$false}
try{
 $games=(Get-Content "$PSScriptRoot\backend\games.json" -Raw|ConvertFrom-Json).games
 if($request.action -eq 'scan'){
  $sources=Get-SharingSources $request
  $selectedId=[string]$request.scanAccountId
  if($selectedId){$chosen=@($sources|Where-Object { [string]$_.id -ceq $selectedId })}
  else{$chosen=@($sources)}
  if(!$chosen.Count){throw 'Selected account is unavailable. Use Add Account to sign in again.'}
  $script:BulkRequestDelayMs=0
  $cancelPath=if([string]::IsNullOrWhiteSpace([string]$request.cancelPath)){Join-Path $job 'cancel.flag'}else{[string]$request.cancelPath}
  $summaries=@();$failedAccounts=@()
  foreach($source in $chosen){
   Select-SharingSource $source
   Send-GuiLog ('Scanning licenses for '+$source.email+'.')
   $cooldownAccount=if($source.id){[string]$source.email}else{''}
   $ignoreCooldown=([bool]$request.scanOnAdd) -and $selectedId -and ([string]$source.id -ceq $selectedId)
   try{$data=Get-AllGuiReceipts $games $cancelPath -CooldownAccount $cooldownAccount -IgnoreCooldown:$ignoreCooldown}catch{$failedAccounts+=,[pscustomobject]@{email=$source.email;message=(Protect-LogText $_.Exception.Message)};continue}
   $summaries+=,$data.bulk
   if($data.bulk.throttled -or $data.bulk.cancelled){break}
  }
  $result.success=$true;$result.data=@{accountsScanned=$summaries.Count;summaries=$summaries;failedAccounts=$failedAccounts}
 }elseif($request.action -in @('import','validate')){
  $path=Join-Path $job 'received-private.bin'
  $bytes=[Convert]::FromBase64String([string]$request.receipt)
  if($bytes.Length -gt 1048576){throw 'Received receipt is too large.'}
  [IO.File]::WriteAllBytes($path,$bytes)
  $identified=Find-ReceiptGame $path $games
  if([int]$identified.index -ne [int]$request.gameId){throw 'Received receipt belongs to another game.'}
  if($request.action -eq 'import'){
   $data=Import-GuiReceipt $path $games
   $result.success=$true;$result.data=$data
  }else{
   $script:TrustedReceiptHash=$null;$receipt=Read-Receipt $path $identified
   if(!$receipt.Valid){throw 'Received receipt has expired or its timestamp is in the future.'}
   $normalized=ConvertTo-GameReceiptBytes $receipt.Bytes
   if($normalized.Length -ne $receipt.Bytes.Length){
    [IO.File]::WriteAllBytes($path,$normalized)
    $script:TrustedReceiptHash=$null;$receipt=Read-Receipt $path $identified
    if(!$receipt.Valid){throw 'Normalized receipt is invalid.'}
   }
   $result.success=$true;$result.data=@{gameId=[int]$identified.index;licenseType=$receipt.LicenseType;expiresUtc=$receipt.Expires.ToUniversalTime().ToString('o');receipt=[Convert]::ToBase64String($normalized)}
  }
 }elseif($request.action -in @('inventory','receipt')){
  $sources=Get-SharingSources $request
  $offers=@{};$owners=@{};$accountLicenses=@();$mainProofs=@{}
  foreach($source in $sources){
   $proofs=Get-SourceProofs $games $source
   $mirrored=0
   if([string]::IsNullOrWhiteSpace([string]$source.id)){$mainProofs=$proofs}
   else{
    foreach($key in @($proofs.Keys)){
     $id=[int]$key
     if(!$mainProofs.ContainsKey($id)){continue}
     $altReceipt=$proofs[$id];$mainReceipt=$mainProofs[$id]
     if($altReceipt.PurchaseDate -and $mainReceipt.PurchaseDate -and $altReceipt.PurchaseDate -eq $mainReceipt.PurchaseDate -and $altReceipt.LicenseType -eq $mainReceipt.LicenseType){
      $proofs.Remove($id);$mirrored++
     }
    }
   }
   $licenseCount=@($proofs.Values|Where-Object {$_.LicenseType -in @('Full','Trial')}).Count
   $accountLicenses+=,[pscustomobject]@{email=$source.email;count=$licenseCount;mirrored=$mirrored}
   foreach($key in $proofs.Keys){
    $id=[int]$key;$signed=$proofs[$id]
    if(!$offers.ContainsKey($id)){
     $game=$games|Where-Object index -eq $id|Select-Object -First 1
     $offers[$id]=[pscustomobject]@{gameId=$id;name=($game.name+$(if($signed.LicenseType -eq 'Trial'){' [Trial]'}else{''}));licenseType=$signed.LicenseType;fresh=[bool]$signed.Valid;expiresUtc=$signed.Expires.ToUniversalTime().ToString('o')}
    }
    if(!$owners.ContainsKey($id)){$owners[$id]=@()}
    $owners[$id]+=$source
   }
  }
  if($request.action -eq 'inventory'){
   $result.success=$true;$result.data=@{accountName=(($sources|ForEach-Object email) -join ', ');primaryAccount=$sources[0].email;inactiveAccounts=@($script:InactiveSharingAccounts);accountCount=$sources.Count;accountLicenses=@($accountLicenses);games=@($offers.Values);totalGames=$games.Count}
  }else{
   $id=[int]$request.gameId;$game=$games|Where-Object index -eq $id|Select-Object -First 1
   if(!$game -or !$owners.ContainsKey($id)){throw 'This game has no verified locally acquired entitlement record for an active account. Scan its owning account first.'}
   $primary=@($owners[$id]|Where-Object {[string]::IsNullOrEmpty([string]$_.id)})
   $source=if($primary.Count){$primary[0]}else{@($owners[$id]|Get-Random -Count 1)[0]}
   Select-SharingSource $source
   # Each transfer mints a new receipt using the selected account. No account picker is shown.
   $signed=Request-GameReceipt $game $job
   $script:TrustedReceiptHash=$null;$signed=Read-Receipt $signed.Path $game
   if(!$signed.SignatureVerified -or !$signed.Valid){throw 'The newly requested Store receipt is invalid or outside its 24-hour validity window.'}
   $run=New-GuiGameRun $game 'Receipts' $signed;$target=Join-Path $run 'pop.bin'
   Copy-Item -LiteralPath $signed.Path -Destination $target;$signed.Path=$target
   $null=Save-ReceiptProof $game $signed 'minted' $run
   Send-GuiLog ('Shared '+(Get-GuiReceiptType $signed)+' receipt saved.')
   $result.success=$true;$result.data=@{gameId=$id;receipt=[Convert]::ToBase64String($signed.Bytes);expiresUtc=$signed.Expires.ToUniversalTime().ToString('o')}
  }
 }else{throw 'Unsupported sharing operation.'}
}catch{
 $result.message=Protect-LogText $_.Exception.Message
 $result.needsSignIn=($result.message -notlike '*No license*' -and $request.action -ne 'import')
}finally{
 $result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $job 'result-private.json') -Encoding UTF8
 $script:EventWriter.Dispose();$stream.Dispose()
}
