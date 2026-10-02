using Features.Application.DataAccess;
using Microsoft.AspNetCore.Mvc;

namespace Features.Controllers;

[ApiController]
[Route("api/data")]
public sealed class DataAccessController(
    IMetadataDataEngine dataEngine,
    ICommandService commandService) : ControllerBase
{
    [HttpPost("query")]
    public async Task<IActionResult> Query(
        [FromBody] QueryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await dataEngine.ExecuteQueryAsync(request, cancellationToken));
        }
        catch (EntityNotAllowedException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
        }
        catch (PropertyNotAllowedException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (DataEngineValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("command")]
    public async Task<IActionResult> Command(
        [FromBody] CommandRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandService.ExecuteAsync(request, cancellationToken);
            return result.Succeeded ? Ok(result) : BadRequest(result);
        }
        catch (EntityNotAllowedException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
        }
        catch (PropertyNotAllowedException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (DataEngineValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }
}
