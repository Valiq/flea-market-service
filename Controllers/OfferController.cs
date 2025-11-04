using flea_market_service.Core.Services;
using Microsoft.AspNetCore.Mvc;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Controllers
{
    [ApiController]
    public class OfferController : Controller
    {
        private readonly OfferService service;

        public OfferController(IConfiguration configuration)
        {
            service = new OfferService(configuration);
        }

        [HttpPost("api/v1/offers")]
        public async Task<IActionResult> GetOffers([FromHeader] string Token, [FromBody] OfferRequest request)
        {
            try
            {
                var result = await service.GetOffersPaged(request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("api/v1/offers/{id}")]
        public async Task<IActionResult> GetOffer([FromHeader] string Token, [FromRoute] long id)
        {
            try
            {
                var result = await service.GetOffer(id, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPatch("api/v1/offers/status")]
        public async Task<IActionResult> UpdateOfferStatus([FromHeader] string Token, [FromBody] OfferStatusRequest request)
        {
            try
            {
                var result = await service.UpdateOffersStatus(request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
