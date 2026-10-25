<#
.SYNOPSIS
    Runs automated Mutation Testing on GoRide Microservices using Stryker.NET.
.DESCRIPTION
    Executes Stryker.NET on the targeted FareCalculationService domain logic,
    evaluating mutant survival and generating HTML/JSON reports.
#>

param (
    [switch]$OpenReport
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  GoRide Microservice Quality Pipeline - Mutation Testing  " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Ensure dotnet-stryker is available
if (-not (Get-Command dotnet-stryker -ErrorAction SilentlyContinue)) {
    Write-Host "[INFO] Installing dotnet-stryker global tool..." -ForegroundColor Yellow
    dotnet tool install -g dotnet-stryker
}

$TestDir = Join-Path $PSScriptRoot "tests\GoRide.Trip.Tests"

Write-Host "[INFO] Navigating to test project: $TestDir" -ForegroundColor Green
Push-Location $TestDir

try {
    Write-Host "[INFO] Executing Stryker.NET mutation test suite..." -ForegroundColor Green
    if ($OpenReport) {
        dotnet stryker -o
    } else {
        dotnet stryker
    }
}
finally {
    Pop-Location
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Mutation Testing Execution Completed Successfully      " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
