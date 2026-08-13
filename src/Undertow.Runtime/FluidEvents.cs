namespace Undertow.Runtime;

/// <summary>The Fluid event-name vocabulary (dewdrop/events).</summary>
public static class FluidEvents
{
    public const string ConnectDocument = "connect_document";
    public const string ConnectDocumentSuccess = "connect_document_success";
    public const string ConnectDocumentError = "connect_document_error";
    public const string SubmitOp = "submitOp";
    public const string SubmitSignal = "submitSignal";
    public const string Op = "op";
    public const string Signal = "signal";
    public const string Nack = "nack";
    public const string Close = "close";
    public const string SubmitSummary = "submitSummary";
    public const string SummaryAck = "summaryAck";
    public const string SummaryNack = "summaryNack";
}
