using System.ComponentModel.DataAnnotations;

namespace Caligo.Api.Dtos;

public class CommentLabelRequest
{
    [Required]
    [RegularExpression("^(spam|toxic|ok)$")]
    public string Label { get; set; } = string.Empty;
}

public class GenreLabelRequest
{
    [Required]
    [StringLength(600, MinimumLength = 1)]
    public string Uri { get; set; } = string.Empty;

    [Required]
    [StringLength(32, MinimumLength = 1)]
    public string Genre { get; set; } = string.Empty;
}
