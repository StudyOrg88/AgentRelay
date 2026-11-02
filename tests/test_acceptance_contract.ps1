$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$schemaPath = Join-Path $root 'docs/acceptance-schema-v1.json'
$examplePath = Join-Path $root 'examples/acceptance-record-v1.json'
$migrationPath = Join-Path $root 'docs/acceptance-v1-migration.md'

foreach ($path in @($schemaPath, $examplePath, $migrationPath)) {
    if (-not (Test-Path $path)) { throw "Missing acceptance v1 artifact: $path" }
}

$schemaText = Get-Content $schemaPath -Raw
$exampleText = Get-Content $examplePath -Raw
$schema = $schemaText | ConvertFrom-Json
if (-not ($exampleText | Test-Json -Schema $schemaText)) { throw 'Example does not validate against schema' }
$invalid = $exampleText | ConvertFrom-Json
$invalid.reviewed_code_commit = 'abc1234'
if (($invalid | ConvertTo-Json -Depth 20) | Test-Json -Schema $schemaText -ErrorAction SilentlyContinue) { throw 'Schema must reject abbreviated SHA' }
$required = @($schema.required)
foreach ($field in @('reviewed_code_commit','acceptance_base_commit','record_commit_parent','items')) {
    if ($required -notcontains $field) { throw "Schema does not require $field" }
}

$itemRequired = @($schema.properties.items.items.required)
foreach ($field in @('status','merge_blocking','evidence_type','accepted_evidence','rejected_evidence','owner_task')) {
    if ($itemRequired -notcontains $field) { throw "Acceptance item does not require $field" }
}

$record = Get-Content $examplePath -Raw | ConvertFrom-Json
foreach ($field in @('reviewed_code_commit','acceptance_base_commit','record_commit_parent')) {
    if (-not $record.$field) { throw "Example record missing $field" }
}
if ($record.PSObject.Properties.Name -contains 'record_commit_sha') { throw 'Acceptance record must not self-report its own commit SHA' }

$migration = Get-Content $migrationPath -Raw
foreach ($phrase in @('不得静默改写','已批准','新增','版本化')) {
    if ($migration -notmatch $phrase) { throw "Migration guidance missing: $phrase" }
}
Write-Output 'PASS acceptance schema contract'
