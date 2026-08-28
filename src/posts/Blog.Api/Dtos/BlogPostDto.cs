using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

public class BlogPostDto
{
    [Required]
    public string Title { get; set; } = string.Empty;
    [Required]
    public string Content { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new List<string>();
}
