using System.IO;
using FluentAssertions;
using LegalAI.Api.Controllers;
using LegalAI.Application.Commands;
using LegalAI.Domain.Entities;
using LegalAI.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using System.Security.Claims;

namespace LegalAI.UnitTests.Api;

public sealed class DocumentsControllerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IDocumentStore> _docStore = new();
    private readonly Mock<IVectorStore> _vectorStore = new();
    private readonly Mock<IUserDomainGrantStore> _domainGrants = new();
    private readonly Mock<IDatasetStore> _datasetStore = new();
    private readonly Mock<IDomainModuleRegistry> _domainRegistry = new();
    private readonly DocumentsController _sut;

    public DocumentsControllerTests()
    {
        _domainRegistry.SetupGet(x => x.ActiveDomainId).Returns("legal");

        _sut = new DocumentsController(
            _mediator.Object,
            _docStore.Object,
            _vectorStore.Object,
            _domainGrants.Object,
            _datasetStore.Object,
            _domainRegistry.Object);

        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Role, "Admin")
                    ],
                    "TestAuth"))
            }
        };
    }

    // ─── GetDocuments ────────────────────────────────────────────

    [Fact]
    public async Task GetDocuments_EmptyStore_ReturnsEmptyList()
    {
        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<LegalDocument>());

        var result = await _sut.GetDocuments(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var docs = ok.Value.Should().BeAssignableTo<IEnumerable<DocumentDto>>().Subject;
        docs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDocuments_WithDocs_MapsAllFields()
    {
        var doc = new LegalDocument
        {
            FilePath = @"C:\data\law.pdf",
            FileName = "law.pdf",
            ContentHash = "abc123",
            Status = DocumentStatus.Indexed,
            PageCount = 42,
            ChunkCount = 120,
            FileSizeBytes = 1024_000,
            CaseNamespace = "ns-1"
        };

        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<LegalDocument> { doc });

        var result = await _sut.GetDocuments(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var docs = ok.Value.Should().BeAssignableTo<IEnumerable<DocumentDto>>().Subject.ToList();
        docs.Should().HaveCount(1);

        var dto = docs[0];
        dto.FileName.Should().Be("law.pdf");
        dto.Status.Should().Be("Indexed");
        dto.PageCount.Should().Be(42);
        dto.ChunkCount.Should().Be(120);
        dto.FileSizeBytes.Should().Be(1024_000);
        dto.CaseNamespace.Should().Be("ns-1");
    }

    [Fact]
    public async Task GetDocuments_FailedDoc_MapsErrorMessage()
    {
        var doc = new LegalDocument
        {
            FilePath = @"C:\bad.pdf",
            FileName = "bad.pdf",
            ContentHash = "fail",
            Status = DocumentStatus.Failed,
            ErrorMessage = "corrupt PDF"
        };

        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<LegalDocument> { doc });

        var result = await _sut.GetDocuments(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var docs = ok.Value.Should().BeAssignableTo<IEnumerable<DocumentDto>>().Subject.ToList();
        docs[0].ErrorMessage.Should().Be("corrupt PDF");
        docs[0].Status.Should().Be("Failed");
    }

    // ─── IngestDocument ──────────────────────────────────────────

    [Fact]
    public async Task IngestDocument_EmptyFilePath_Returns400()
    {
        var result = await _sut.IngestDocument(
            new IngestRequest { FilePath = "" }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task IngestDocument_WhitespaceFilePath_Returns400()
    {
        var result = await _sut.IngestDocument(
            new IngestRequest { FilePath = "   " }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task IngestDocument_FileNotFound_Returns400()
    {
        var result = await _sut.IngestDocument(
            new IngestRequest { FilePath = @"C:\nonexistent\file.pdf" }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task IngestDocument_Success_Returns200WithDocumentId()
    {
        // Create a temporary file so File.Exists passes
        var tmpFile = Path.GetTempFileName();
        try
        {
            _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new IngestDocumentResult
                     {
                         Success = true,
                         DocumentId = "doc-1",
                         ChunksCreated = 15,
                         LatencyMs = 320
                     });

            var result = await _sut.IngestDocument(
                new IngestRequest { FilePath = tmpFile, CaseNamespace = "ns-1", UserId = "user-1" },
                CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task IngestDocument_Failure_Returns422()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new IngestDocumentResult
                     {
                         Success = false,
                         Error = "corrupt PDF",
                         LatencyMs = 50
                     });

            var result = await _sut.IngestDocument(
                new IngestRequest { FilePath = tmpFile }, CancellationToken.None);

            result.Should().BeOfType<UnprocessableEntityObjectResult>();
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task IngestDocument_ForwardsFieldsToCommand()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            IngestDocumentCommand? captured = null;
            _mediator.Setup(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()))
                     .Callback<IRequest<IngestDocumentResult>, CancellationToken>((c, _) =>
                         captured = (IngestDocumentCommand)c)
                     .ReturnsAsync(new IngestDocumentResult { Success = true });

            await _sut.IngestDocument(
                new IngestRequest { FilePath = tmpFile, CaseNamespace = "ns-x", UserId = "u-1" },
                CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.FilePath.Should().Be(tmpFile);
            captured.CaseNamespace.Should().Be("ns-x");
            captured.UserId.Should().Be("u-1");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ─── IngestDirectory ─────────────────────────────────────────

    [Fact]
    public async Task IngestDirectory_EmptyPath_Returns400()
    {
        var result = await _sut.IngestDirectory(
            new IngestDirectoryRequest { DirectoryPath = "" }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task IngestDirectory_NonexistentDirectory_Returns400()
    {
        var result = await _sut.IngestDirectory(
            new IngestDirectoryRequest { DirectoryPath = @"C:\surely\does\not\exist_xyz" },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task IngestDirectory_Success_Returns200WithStats()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            _mediator.Setup(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new IngestDirectoryResult
                     {
                         TotalFiles = 10,
                         SuccessCount = 8,
                         FailedCount = 1,
                         SkippedCount = 1,
                         FailedFiles = ["bad.pdf"],
                         TotalLatencyMs = 5000
                     });

            var result = await _sut.IngestDirectory(
                new IngestDirectoryRequest { DirectoryPath = tmpDir },
                CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task IngestDirectory_ForwardsRecursiveFlag()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            IngestDirectoryCommand? captured = null;
            _mediator.Setup(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()))
                     .Callback<IRequest<IngestDirectoryResult>, CancellationToken>((c, _) =>
                         captured = (IngestDirectoryCommand)c)
                     .ReturnsAsync(new IngestDirectoryResult());

            await _sut.IngestDirectory(
                new IngestDirectoryRequest { DirectoryPath = tmpDir, Recursive = false },
                CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.Recursive.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public async Task IngestDirectory_DefaultRecursiveIsTrue()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            IngestDirectoryCommand? captured = null;
            _mediator.Setup(m => m.Send(It.IsAny<IngestDirectoryCommand>(), It.IsAny<CancellationToken>()))
                     .Callback<IRequest<IngestDirectoryResult>, CancellationToken>((c, _) =>
                         captured = (IngestDirectoryCommand)c)
                     .ReturnsAsync(new IngestDirectoryResult());

            await _sut.IngestDirectory(
                new IngestDirectoryRequest { DirectoryPath = tmpDir },
                CancellationToken.None);

            captured!.Recursive.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    // ─── GetQuarantine ───────────────────────────────────────────

    [Fact]
    public async Task GetQuarantine_ReturnsRecords()
    {
        var records = new List<QuarantineRecord>
        {
            new()
            {
                DocumentId = "d-1",
                FilePath = "bad.pdf",
                Reason = "corrupt",
                FailureCount = 3,
                ContentHash = "hash-1"
            }
        };
        _docStore.Setup(d => d.GetQuarantineRecordsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(records);

        var result = await _sut.GetQuarantine(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeAssignableTo<List<QuarantineRecord>>().Subject;
        returned.Should().HaveCount(1);
        returned[0].DocumentId.Should().Be("d-1");
    }

    [Fact]
    public async Task GetQuarantine_Empty_ReturnsEmptyList()
    {
        _docStore.Setup(d => d.GetQuarantineRecordsAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<QuarantineRecord>());

        var result = await _sut.GetQuarantine(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeAssignableTo<List<QuarantineRecord>>().Subject;
        returned.Should().BeEmpty();
    }

    // ─── GetStats ────────────────────────────────────────────────

    [Fact]
    public async Task GetStats_ReturnsAggregatedStats()
    {
        _docStore.Setup(d => d.GetDocumentCountAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(42);
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(1200);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new VectorStoreHealth { IsHealthy = true, Status = "OK" });

        var result = await _sut.GetStats(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetStats_UnhealthyVectorStore_StillReturns200()
    {
        _docStore.Setup(d => d.GetDocumentCountAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(0);
        _vectorStore.Setup(v => v.GetVectorCountAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(0);
        _vectorStore.Setup(v => v.GetHealthAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new VectorStoreHealth { IsHealthy = false, Status = "Not initialized" });

        var result = await _sut.GetStats(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task IngestDocument_NonAdminWithoutDomainGrant_ReturnsForbid()
    {
        var nonAdminController = new DocumentsController(
            _mediator.Object,
            _docStore.Object,
            _vectorStore.Object,
            _domainGrants.Object,
            _datasetStore.Object,
            _domainRegistry.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, "user-1"),
                            new Claim(ClaimTypes.Role, "Analyst")
                        ],
                        "TestAuth"))
                }
            }
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            _domainGrants
                .Setup(g => g.HasAccessAsync("user-1", "legal", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var result = await nonAdminController.IngestDocument(
                new IngestRequest { FilePath = tmpFile, DomainId = "legal" },
                CancellationToken.None);

            result.Should().BeOfType<ForbidResult>();
            _mediator.Verify(m => m.Send(It.IsAny<IngestDocumentCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task GetDocuments_NonAdmin_OnlyReturnsGrantedDomain()
    {
        var nonAdminController = new DocumentsController(
            _mediator.Object,
            _docStore.Object,
            _vectorStore.Object,
            _domainGrants.Object,
            _datasetStore.Object,
            _domainRegistry.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, "user-1"),
                            new Claim(ClaimTypes.Role, "Analyst")
                        ],
                        "TestAuth"))
                }
            }
        };

        _docStore.Setup(d => d.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new LegalDocument
                {
                    Id = "doc-legal",
                    FilePath = @"C:\legal.pdf",
                    FileName = "legal.pdf",
                    ContentHash = "hash-1",
                    DomainId = "legal",
                    Status = DocumentStatus.Indexed
                },
                new LegalDocument
                {
                    Id = "doc-med",
                    FilePath = @"C:\medical.pdf",
                    FileName = "medical.pdf",
                    ContentHash = "hash-2",
                    DomainId = "medical",
                    Status = DocumentStatus.Indexed
                }
            ]);

        _domainGrants.Setup(g => g.GetForUserAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new UserDomainGrant
                {
                    UserId = "user-1",
                    DomainId = "legal"
                }
            ]);

        _datasetStore.Setup(d => d.GetAllAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await nonAdminController.GetDocuments(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var docs = ok.Value.Should().BeAssignableTo<IEnumerable<DocumentDto>>().Subject.ToList();
        docs.Should().HaveCount(1);
        docs[0].Id.Should().Be("doc-legal");
    }
}
