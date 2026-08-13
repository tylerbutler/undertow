namespace Undertow.Protocol

/// Port of spillway/summary: summary protocol types.
module Summary =

    type SummaryType =
        | Tree
        | Blob
        | Attachment

    type SummaryObject =
        | SummaryBlob of content: string
        | SummaryHandle of handle: string * handleType: SummaryType
        | SummaryAttachment of id: string
        | SummaryTreeNode of tree: Map<string, SummaryObject>

    type SummaryTree = { Tree: Map<string, SummaryObject> }

    type SummaryOp =
        {
            ParentSummaryHandle: string
            SummaryTree: SummaryTree
            SequenceNumber: int64
        }

    /// Contents of a summarize message as submitted by client.
    type SummarizeContents =
        {
            Handle: string
            Message: string
            Parents: string list
            Head: string
            IncludesProtocolTree: bool option
        }

    type SummaryAck =
        {
            Handle: string
            SummarySequenceNumber: int64
        }

    type SummaryNack =
        {
            SummarySequenceNumber: int64
            Code: int option
            Message: string option
            RetryAfter: int64 option
        }

    /// Pending summary tracking state.
    type PendingSummary =
        {
            ClientId: string
            Contents: SummarizeContents
            SequenceNumber: int64
            Timestamp: int64
        }

    type SummaryContext = Message.SummaryContext

    let summaryTypeToString st =
        match st with
        | Tree -> "tree"
        | Blob -> "blob"
        | Attachment -> "attachment"

    let summaryTypeFromString s =
        match s with
        | "tree" -> Some Tree
        | "blob" -> Some Blob
        | "attachment" -> Some Attachment
        | _ -> None

    /// Numeric type codes from the ISummaryTree interface.
    let summaryTypeToCode st =
        match st with
        | Tree -> 1
        | Blob -> 2
        | Attachment -> 4

    let summaryTypeFromCode code =
        match code with
        | 1 -> Some Tree
        | 2 -> Some Blob
        | 4 -> Some Attachment
        | _ -> None

    let emptySummaryTree () : SummaryTree = { Tree = Map.empty }

    let newSummaryTree entries : SummaryTree = { Tree = Map.ofList entries }

    let addToSummaryTree (summary: SummaryTree) path obj : SummaryTree =
        { Tree = Map.add path obj summary.Tree }

    let getFromSummaryTree (summary: SummaryTree) path = Map.tryFind path summary.Tree

    let createSummaryAck handle sequenceNumber : SummaryAck =
        {
            Handle = handle
            SummarySequenceNumber = sequenceNumber
        }

    let createSummaryNack sequenceNumber code message : SummaryNack =
        {
            SummarySequenceNumber = sequenceNumber
            Code = code
            Message = message
            RetryAfter = None
        }

    let createSummaryNackWithRetry sequenceNumber code message retryAfter : SummaryNack =
        {
            SummarySequenceNumber = sequenceNumber
            Code = code
            Message = message
            RetryAfter = Some retryAfter
        }

    let createSummaryContext handle sequenceNumber : Message.SummaryContext =
        {
            Handle = handle
            SequenceNumber = sequenceNumber
        }
