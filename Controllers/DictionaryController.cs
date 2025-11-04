using flea_market_service.Core.Services;
using flea_market_service.Models;
using Microsoft.AspNetCore.Mvc;

namespace flea_market_service.Controllers
{
    [ApiController]
    public class DictionaryController : Controller
    {
        private readonly DictionaryService service;

        public DictionaryController(IConfiguration configuration)
        {
            service = new DictionaryService(configuration);
        }

        [HttpGet("api/v1/dictionaries")]
        public async Task<IActionResult> GetUserRoles([FromHeader] string Token, [FromQuery] string dictionary)
        {
            try
            {
                var result = await service.GetDictionary(dictionary, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
