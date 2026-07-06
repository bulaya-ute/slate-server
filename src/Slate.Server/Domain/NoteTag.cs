namespace Slate.Server.Domain;

/// <summary>Join table between notes and tags. Table: note_tags. Composite key (note_id, tag_id).</summary>
public class NoteTag
{
    public Guid NoteId { get; set; }
    public Guid TagId { get; set; }

    public Note? Note { get; set; }
    public Tag? Tag { get; set; }
}
