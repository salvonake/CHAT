using FluentAssertions;
using LegalAI.Api.Controllers;
using LegalAI.Application.Queries;
using LegalAI.Domain.Interfaces;
using LegalAI.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace LegalAI.UnitTests.Api;

public sealed class AskControllerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ILogger<AskController>> _logger = new();
    private readonly AskController _sut;

    public AskControllerTests()
    {
        _sut = new AskController(_mediator.Object, _logger.Object);
    }

    private static LegalAnswer MakeAnswer(
        string answer = "إجابة اختبارية",
        double confidence = 0.9,
        bool isAbstention = false) => new()
    {
        Answer = answer,
        Citations =
        [
            new Citation
            {
                Document = "law.pdf",
                Page = 5,
                Section = "المادة 42",
                Snippet = "نص الاقتباس",
                ArticleReference = "المادة 42",
                CaseNumber = "1234/2024",
                SimilarityScore = 0.92
            }
        ],
        ConfidenceScore = confidence,
        RetrievedChunksUsed = 3,
        RetrievalSimilarityAvg = 0.88,
        IsAbstention = isAbstention,
        AbstentionReason = isAbstention ? "عدم كفاية الأدلة" : null,
        Warnings = confidence < 0.5 ? ["ثقة منخفضة"] : [],
        GenerationLatencyMs = 250,
        RetrievalLatencyMs = 80
    };

    // ─── Validation ──────────────────────────────────────────────

    [Fact]
    public async Task Ask_EmptyQuestion_Returns400()
    {
        var result = await _sut.Ask(new AskRequest { Question = "" }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Ask_WhitespaceQuestion_Returns400()
    {
        var result = await _sut.Ask(new AskRequest { Question = "   " }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Ask_NullQuestion_Returns400()
    {
        var result = await _sut.Ask(new AskRequest(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ─── Happy path ──────────────────────────────────────────────

    [Fact]
    public async Task Ask_ValidQuestion_Returns200WithAskResponse()
    {
        var answer = MakeAnswer();
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(answer);

        var result = await _sut.Ask(
            new AskRequest { Question = "ما هو الحكم؟" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<AskResponse>().Subject;

        response.Answer.Should().Be(answer.Answer);
        response.ConfidenceScore.Should().Be(answer.ConfidenceScore);
        response.RetrievedChunksUsed.Should().Be(answer.RetrievedChunksUsed);
        response.RetrievalSimilarityAvg.Should().Be(answer.RetrievalSimilarityAvg);
        response.IsAbstention.Should().Be(answer.IsAbstention);
        response.GenerationLatencyMs.Should().Be(answer.GenerationLatencyMs);
        response.RetrievalLatencyMs.Should().Be(answer.RetrievalLatencyMs);
    }

    [Fact]
    public async Task Ask_ValidQuestion_MapsCitationsCorrectly()
    {
        var answer = MakeAnswer();
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(answer);

        var result = await _sut.Ask(
            new AskRequest { Question = "ما هو الحكم؟" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<AskResponse>().Subject;

        response.Citations.Should().HaveCount(1);
        var citation = response.Citations[0];
        citation.Document.Should().Be("law.pdf");
        citation.Page.Should().Be(5);
        citation.Section.Should().Be("المادة 42");
        citation.Snippet.Should().Be("نص الاقتباس");
        citation.ArticleReference.Should().Be("المادة 42");
        citation.CaseNumber.Should().Be("1234/2024");
        citation.SimilarityScore.Should().Be(0.92);
    }

    // ─── Query mapping ──────────────────────────────────────────

    [Fact]
    public async Task Ask_PassesQuestionToMediator()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(new AskRequest { Question = "سؤال" }, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Question.Should().Be("سؤال");
    }

    [Fact]
    public async Task Ask_DefaultStrictModeIsTrue()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(new AskRequest { Question = "سؤال" }, CancellationToken.None);

        captured!.StrictMode.Should().BeTrue();
    }

    [Fact]
    public async Task Ask_DefaultTopKIs10()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(new AskRequest { Question = "سؤال" }, CancellationToken.None);

        captured!.TopK.Should().Be(10);
    }

    [Fact]
    public async Task Ask_ExplicitStrictModeFalse_Forwarded()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(new AskRequest { Question = "سؤال", StrictMode = false }, CancellationToken.None);

        captured!.StrictMode.Should().BeFalse();
    }

    [Fact]
    public async Task Ask_ExplicitTopK_Forwarded()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(new AskRequest { Question = "سؤال", TopK = 5 }, CancellationToken.None);

        captured!.TopK.Should().Be(5);
    }

    [Fact]
    public async Task Ask_CaseNamespaceForwarded()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(
            new AskRequest { Question = "سؤال", CaseNamespace = "ns-1" },
            CancellationToken.None);

        captured!.CaseNamespace.Should().Be("ns-1");
    }

    [Fact]
    public async Task Ask_UserIdForwarded()
    {
        AskLegalQuestionQuery? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .Callback<IRequest<LegalAnswer>, CancellationToken>((q, _) =>
                     captured = (AskLegalQuestionQuery)q)
                 .ReturnsAsync(MakeAnswer());

        await _sut.Ask(
            new AskRequest { Question = "سؤال", UserId = "user-42" },
            CancellationToken.None);

        captured!.UserId.Should().Be("user-42");
    }

    // ─── Abstention mapping ─────────────────────────────────────

    [Fact]
    public async Task Ask_AbstentionAnswer_MapsAbstentionFields()
    {
        var answer = MakeAnswer(isAbstention: true, confidence: 0);
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(answer);

        var result = await _sut.Ask(
            new AskRequest { Question = "سؤال" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<AskResponse>().Subject;

        response.IsAbstention.Should().BeTrue();
        response.AbstentionReason.Should().Be("عدم كفاية الأدلة");
    }

    // ─── Warnings ────────────────────────────────────────────────

    [Fact]
    public async Task Ask_LowConfidence_MapsWarnings()
    {
        var answer = MakeAnswer(confidence: 0.3);
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(answer);

        var result = await _sut.Ask(
            new AskRequest { Question = "سؤال" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<AskResponse>().Subject;

        response.Warnings.Should().NotBeEmpty();
    }

    // ─── Empty citations ─────────────────────────────────────────

    [Fact]
    public async Task Ask_NoCitations_ReturnEmptyList()
    {
        var answer = new LegalAnswer
        {
            Answer = "لا توجد معلومات",
            Citations = [],
            ConfidenceScore = 0,
            RetrievedChunksUsed = 0,
            RetrievalSimilarityAvg = 0,
            IsAbstention = true,
            AbstentionReason = "لا وثائق"
        };
        _mediator.Setup(m => m.Send(It.IsAny<AskLegalQuestionQuery>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(answer);

        var result = await _sut.Ask(
            new AskRequest { Question = "سؤال" }, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<AskResponse>().Subject;

        response.Citations.Should().BeEmpty();
    }
}
