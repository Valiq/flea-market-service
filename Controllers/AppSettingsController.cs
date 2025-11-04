using flea_market_service.Core.Services;
using flea_market_service.Models;
using Microsoft.AspNetCore.Mvc;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Controllers
{
    public class AppSettingsController : Controller
    {
        private readonly AppSettingsService service;

        public AppSettingsController(IConfiguration configuration)
        {
            service = new AppSettingsService(configuration);
        }

        [HttpGet("api/v1/app-settings")]
        public async Task<IActionResult> GetAppSettings([FromHeader] string Token)
        {
            try
            {
                var result = await service.GetAppSettings(Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPatch("api/v1/app-settings/{id}")]
        public async Task<IActionResult> UpdateAppSettings([FromHeader] string Token, [FromRoute] long id, [FromBody] AppSettingsRequest request)
        {
            try
            {
                var result = await service.UpdateAppSettings(id, request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
