namespace Slate.Server.Domain;

/// <summary>
/// A wikilink extracted from a note's content. Table: links. Powers backlinks + the graph view.
/// A surrogate <see cref="Id"/> is added beyond the spec's column list because (source_note_id,
/// target_note_id, target_text) is not guaranteed unique (a note may link the same target twice)
/// and target_note_id is nullable (unresolved links), so no natural key is available.
/// </summary>
public class Link
{
    public Guid Id { get; set; }
    public Guid SourceNoteId { get; set; }

    /// <summary>Null when the link target doesn't (yet) match any note. Resolved lazily.</summary>
    public Guid? TargetNoteId { get; set; }

    /// <summary>The raw wikilink target text, e.g. "folder/note" from [[folder/note|alias]].</summary>
    public required string TargetText { get; set; }

    public Note? SourceNote { get; set; }
    public Note? TargetNote { get; set; }
}
