using flea_market_service.Core.Services;
using flea_market_service.Models;
using Microsoft.AspNetCore.Mvc;
using static flea_market_service.Records.Requests;
using static flea_market_service.Records.Respons;

namespace flea_market_service.Controllers
{
    [ApiController]
    public class ModerationController : Controller
    {
        private readonly ModerationService service;

        public ModerationController(IConfiguration configuration)
        {
            service = new ModerationService(configuration);
        }

        [HttpPost("api/v1/offers/moderations")]
        public async Task<IActionResult> GetOffersModerations([FromHeader] string Token, [FromBody] ModerationRequest request)
        {
            try
            {
                ModerationsResponse result = await service.GetModerationsPaged(request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("api/v1/offers/moderations/{id}")]
        public async Task<IActionResult> GetOffersModeration([FromHeader] string Token, [FromRoute] long id)
        {
            try
            {
                var result = await service.GetModeration(id, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPatch("api/v1/offers/moderations/{id}")]
        public async Task<IActionResult> UpdateOffersModeration([FromHeader] string Token, [FromRoute] long id, [FromBody] ModerationUpdateRequest request)
        {
            try
            {
                var result = await service.UpdateModeration(id, request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
