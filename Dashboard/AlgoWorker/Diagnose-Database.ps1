#!/usr/bin/env pwsh
# Database Diagnostics Script
# Run this to troubleshoot database connection issues
#
# Usage: .\Diagnose-Database.ps1
#
# This script:
# - Checks if SQL Server is running
# - Tests the connection string
# - Lists available SQL Server instances
# - Shows detailed error diagnostics

Write-Host "`n=== NiftyBot Database Diagnostics ===" -ForegroundColor Cyan
Write-Host "Testing database connectivity and configuration...`n" -ForegroundColor Gray

# 1. Check SQL Server services
Write-Host "1. Checking SQL Server Services..." -ForegroundColor Yellow
$services = Get-Service -Name "MSSQL*" -ErrorAction SilentlyContinue
if ($services) {
	$services | ForEach-Object {
		$status = $_.Status -eq "Running" ? "✓ Running" : ("✗ " + $_.Status)
		Write-Host "   $($_.Name): $status"
	}
} else {
	Write-Host "   ⚠ No SQL Server services found" -ForegroundColor Yellow
	Write-Host "   Install SQL Server or enable the service" -ForegroundColor Gray
}

# 2. List SQL Server instances
Write-Host "`n2. Discovering SQL Server Instances..." -ForegroundColor Yellow
$instances = @()
try {
	# Try LocalDB
	$localdbPath = "C:\Program Files\Microsoft SQL Server\110\Tools\Binn\SqlLocalDB.exe"
	if (Test-Path $localdbPath) {
		$localdbInstances = & $localdbPath info
		if ($localdbInstances) {
			Write-Host "   LocalDB Instances:"
			$localdbInstances | ForEach-Object { Write-Host "     - (localdb)\$_" }
			$instances += $localdbInstances | ForEach-Object { "(localdb)\$_" }
		}
	}
} catch {
	Write-Host "   Could not check LocalDB" -ForegroundColor Gray
}

# 3. Test default connection strings
Write-Host "`n3. Testing Connection Strings..." -ForegroundColor Yellow

$connectionStrings = @(
	@{
		Name = "Local SQL Server (localhost)"
		ConnectionString = "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
	},
	@{
		Name = "LocalDB"
		ConnectionString = "Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
	},
	@{
		Name = "Windows Machine Name (from config)"
		ConnectionString = "Server=DESKTOP-BREB3AS;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
	}
)

foreach ($cs in $connectionStrings) {
	Write-Host "   Testing: $($cs.Name)" -ForegroundColor Cyan

	try {
		$connection = New-Object System.Data.SqlClient.SqlConnection $cs.ConnectionString
		$connection.Open()
		$connection.Close()
		Write-Host "     ✓ Success" -ForegroundColor Green
	} catch {
		Write-Host "     ✗ Failed - $($_.Exception.InnerException.Message)" -ForegroundColor Red
	}
}

# 4. Recommendations
Write-Host "`n4. Recommendations" -ForegroundColor Yellow
Write-Host "   • Ensure SQL Server service is running (Services app)"
Write-Host "   • If using Windows Auth, verify user has SQL Server access"
Write-Host "   • Update connection string in appsettings.json if needed"
Write-Host "   • See DATABASE_SETUP.md for detailed troubleshooting"

Write-Host "`nFor questions, check: DATABASE_SETUP.md" -ForegroundColor Gray
Write-Host "="*50 `n
