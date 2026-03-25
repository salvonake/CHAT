# LegalAI - Setup Script
# Prerequisites: .NET 8 SDK, Docker (optional, for Qdrant)
# Run: .\setup.ps1
# This script creates 9 C# projects + 2 test projects,
# wires project references, and installs NuGet packages.

$ErrorActionPreference = "Stop"

Write-Host "=== LegalAI - Evidence-Constrained Legal AI Knowledge Engine ===" -ForegroundColor Cyan
Write-Host "Setting up solution structure..." -ForegroundColor Yellow

# Create solution
dotnet new sln -n LegalAI --force

# Create projects
$projects = @(
    @{ Name = "LegalAI.Domain";        Template = "classlib";  Path = "src/LegalAI.Domain" },
    @{ Name = "LegalAI.Application";   Template = "classlib";  Path = "src/LegalAI.Application" },
    @{ Name = "LegalAI.Ingestion";     Template = "classlib";  Path = "src/LegalAI.Ingestion" },
    @{ Name = "LegalAI.Retrieval";     Template = "classlib";  Path = "src/LegalAI.Retrieval" },
    @{ Name = "LegalAI.Security";      Template = "classlib";  Path = "src/LegalAI.Security" },
    @{ Name = "LegalAI.Infrastructure"; Template = "classlib"; Path = "src/LegalAI.Infrastructure" },
    @{ Name = "LegalAI.Api";           Template = "webapi";    Path = "src/LegalAI.Api" },
    @{ Name = "LegalAI.WorkerService"; Template = "worker";    Path = "src/LegalAI.WorkerService" },
    @{ Name = "LegalAI.Desktop";       Template = "wpf";       Path = "src/LegalAI.Desktop" }
)

foreach ($proj in $projects) {
    Write-Host "Creating $($proj.Name)..." -ForegroundColor Green
    dotnet new $proj.Template -n $proj.Name -o $proj.Path --force
    dotnet sln add "$($proj.Path)/$($proj.Name).csproj"
    # Remove auto-generated files we'll replace
    $autoFiles = @("Class1.cs", "Worker.cs", "Program.cs")
    foreach ($f in $autoFiles) {
        $fp = Join-Path $proj.Path $f
        if (Test-Path $fp) { Remove-Item $fp -Force }
    }
}

# Create test projects
dotnet new xunit -n LegalAI.UnitTests -o tests/LegalAI.UnitTests --force
# Patch UnitTests to target net8.0-windows (required for WPF Desktop reference)
$unitTestCsproj = "tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj"
(Get-Content $unitTestCsproj) -replace '<TargetFramework>net8\.0</TargetFramework>', "<TargetFramework>net8.0-windows</TargetFramework>`n    <UseWPF>true</UseWPF>" | Set-Content $unitTestCsproj
dotnet sln add $unitTestCsproj

dotnet new xunit -n LegalAI.IntegrationTests -o tests/LegalAI.IntegrationTests --force
dotnet sln add tests/LegalAI.IntegrationTests/LegalAI.IntegrationTests.csproj

# Add project references
Write-Host "Adding project references..." -ForegroundColor Yellow

# Application -> Domain
dotnet add src/LegalAI.Application/LegalAI.Application.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj

# Ingestion -> Domain, Application
dotnet add src/LegalAI.Ingestion/LegalAI.Ingestion.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add src/LegalAI.Ingestion/LegalAI.Ingestion.csproj reference src/LegalAI.Application/LegalAI.Application.csproj

# Retrieval -> Domain, Application
dotnet add src/LegalAI.Retrieval/LegalAI.Retrieval.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add src/LegalAI.Retrieval/LegalAI.Retrieval.csproj reference src/LegalAI.Application/LegalAI.Application.csproj

# Security -> Domain
dotnet add src/LegalAI.Security/LegalAI.Security.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj

# Infrastructure -> Domain, Application, Ingestion, Retrieval, Security
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj reference src/LegalAI.Application/LegalAI.Application.csproj
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj reference src/LegalAI.Ingestion/LegalAI.Ingestion.csproj
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj reference src/LegalAI.Retrieval/LegalAI.Retrieval.csproj
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj reference src/LegalAI.Security/LegalAI.Security.csproj

# Api -> all
dotnet add src/LegalAI.Api/LegalAI.Api.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add src/LegalAI.Api/LegalAI.Api.csproj reference src/LegalAI.Application/LegalAI.Application.csproj
dotnet add src/LegalAI.Api/LegalAI.Api.csproj reference src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj
dotnet add src/LegalAI.Api/LegalAI.Api.csproj reference src/LegalAI.Ingestion/LegalAI.Ingestion.csproj
dotnet add src/LegalAI.Api/LegalAI.Api.csproj reference src/LegalAI.Retrieval/LegalAI.Retrieval.csproj
dotnet add src/LegalAI.Api/LegalAI.Api.csproj reference src/LegalAI.Security/LegalAI.Security.csproj

# WorkerService -> Infrastructure, Ingestion, Domain, Application
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj reference src/LegalAI.Application/LegalAI.Application.csproj
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj reference src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj reference src/LegalAI.Ingestion/LegalAI.Ingestion.csproj
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj reference src/LegalAI.Security/LegalAI.Security.csproj

# Tests -> all source projects
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Application/LegalAI.Application.csproj
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Ingestion/LegalAI.Ingestion.csproj
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Retrieval/LegalAI.Retrieval.csproj
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Security/LegalAI.Security.csproj
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj reference src/LegalAI.Desktop/LegalAI.Desktop.csproj

# Desktop -> Domain, Application, Infrastructure, Ingestion, Retrieval, Security
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj reference src/LegalAI.Domain/LegalAI.Domain.csproj
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj reference src/LegalAI.Application/LegalAI.Application.csproj
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj reference src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj reference src/LegalAI.Ingestion/LegalAI.Ingestion.csproj
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj reference src/LegalAI.Retrieval/LegalAI.Retrieval.csproj
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj reference src/LegalAI.Security/LegalAI.Security.csproj

# Add NuGet packages
Write-Host "Adding NuGet packages..." -ForegroundColor Yellow

# Domain - no external deps

# Application
dotnet add src/LegalAI.Application/LegalAI.Application.csproj package MediatR
dotnet add src/LegalAI.Application/LegalAI.Application.csproj package Microsoft.Extensions.Logging.Abstractions

# Ingestion
dotnet add src/LegalAI.Ingestion/LegalAI.Ingestion.csproj package UglyToad.PdfPig
dotnet add src/LegalAI.Ingestion/LegalAI.Ingestion.csproj package Microsoft.ML.OnnxRuntime
dotnet add src/LegalAI.Ingestion/LegalAI.Ingestion.csproj package Microsoft.Extensions.Logging.Abstractions
dotnet add src/LegalAI.Ingestion/LegalAI.Ingestion.csproj package System.Text.Json

# Retrieval
dotnet add src/LegalAI.Retrieval/LegalAI.Retrieval.csproj package Microsoft.Extensions.Logging.Abstractions
dotnet add src/LegalAI.Retrieval/LegalAI.Retrieval.csproj package Microsoft.Extensions.Caching.Memory

# Security
dotnet add src/LegalAI.Security/LegalAI.Security.csproj package Konscious.Security.Cryptography.Argon2
dotnet add src/LegalAI.Security/LegalAI.Security.csproj package Microsoft.Extensions.Logging.Abstractions
dotnet add src/LegalAI.Security/LegalAI.Security.csproj package System.Text.Json

# Infrastructure
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package Qdrant.Client
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package Microsoft.Extensions.Logging.Abstractions
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package Microsoft.Extensions.Http
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package System.Text.Json
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package Microsoft.Data.Sqlite
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package LLamaSharp --version "0.19.*"
dotnet add src/LegalAI.Infrastructure/LegalAI.Infrastructure.csproj package LLamaSharp.Backend.Cuda12 --version "0.19.*"

# Api
dotnet add src/LegalAI.Api/LegalAI.Api.csproj package MediatR
dotnet add src/LegalAI.Api/LegalAI.Api.csproj package AspNetCoreRateLimit
dotnet add src/LegalAI.Api/LegalAI.Api.csproj package Swashbuckle.AspNetCore

# WorkerService
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj package Microsoft.Extensions.Hosting
dotnet add src/LegalAI.WorkerService/LegalAI.WorkerService.csproj package Microsoft.Extensions.Hosting.WindowsServices

# Tests
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj package Moq
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj package FluentAssertions
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj package coverlet.collector
dotnet add tests/LegalAI.UnitTests/LegalAI.UnitTests.csproj package Microsoft.NET.Test.Sdk

# Desktop
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj package CommunityToolkit.Mvvm --version "8.*"
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj package Microsoft.Extensions.Hosting --version "10.*"
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj package Microsoft.Extensions.DependencyInjection --version "10.*"
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj package Microsoft.Extensions.Logging.Console --version "10.*"
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj package Microsoft.Extensions.Configuration.Json --version "10.*"
dotnet add src/LegalAI.Desktop/LegalAI.Desktop.csproj package MediatR --version "14.*"

Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Green
Write-Host "Solution: 9 projects + 2 test projects" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. dotnet build              — verify compilation" -ForegroundColor White
Write-Host "  2. docker-compose up -d      — start Qdrant (optional for API mode)" -ForegroundColor White
Write-Host "  3. dotnet run --project src/LegalAI.Desktop — launch desktop app" -ForegroundColor White
Write-Host "  4. .\installer\build-installer.ps1 — build MSI installer" -ForegroundColor White
