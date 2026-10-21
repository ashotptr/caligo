using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Catalog;
using Caligo.Api.Dtos;
using Caligo.Catalog.Application.Collections;

namespace Caligo.Api.Controllers;

[Route("api/collections")]
[ApiController]
[Authorize]
[RequireCatalog]
[EnableRateLimiting("reads")]
public class CollectionsController : ApiControllerBase
{
    private readonly CollectionService _collections;

    public CollectionsController(CollectionService collections)
    {
        _collections = collections;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Respond(await _collections.ListAsync(CurrentCaller(), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CollectionNameRequest request, CancellationToken cancellationToken) =>
        Respond(await _collections.CreateAsync(new CollectionNameCommand(request.Name), CurrentCaller(), cancellationToken));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken) =>
        Respond(await _collections.GetAsync(id, CurrentCaller(), cancellationToken));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Rename(int id, CollectionNameRequest request, CancellationToken cancellationToken) =>
        Respond(await _collections.RenameAsync(id, new CollectionNameCommand(request.Name), CurrentCaller(), cancellationToken));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) =>
        Respond(await _collections.DeleteAsync(id, CurrentCaller(), cancellationToken));

    [HttpPost("{id:int}/titles")]
    public async Task<IActionResult> Add(int id, AddTitlesRequest request, CancellationToken cancellationToken) =>
        Respond(await _collections.AddAsync(id, new AddTitlesCommand(request.ContentIds ?? new List<int>()), CurrentCaller(), cancellationToken));

    [HttpDelete("{id:int}/titles/{contentId:int}")]
    public async Task<IActionResult> Remove(int id, int contentId, CancellationToken cancellationToken) =>
        Respond(await _collections.RemoveAsync(id, contentId, CurrentCaller(), cancellationToken));
}
