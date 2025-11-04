using flea_market_service.Core.Services;
using Microsoft.AspNetCore.Mvc;
using static flea_market_service.Records.Requests;

namespace flea_market_service.Controllers
{
    [ApiController]
    public class SellerController : Controller
    {
        private readonly SellerService service;

        public SellerController(IConfiguration configuration)
        {
            service = new SellerService(configuration);
        }

        [HttpPost("api/v1/sellers")]
        public async Task<IActionResult> GetSellers([FromHeader] string Token, [FromBody] SellerRequest request)
        {
            try
            {
                var result = await service.GetSellersPaged(request, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("api/v1/sellers/{id}")]
        public async Task<IActionResult> GetSeller([FromHeader] string Token, [FromRoute] long id)
        {
            try
            {
                var result = await service.GetSellerById(id, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPatch("api/v1/sellers/{id}/active")]
        public async Task<IActionResult> UpdateSeller([FromHeader] string Token, [FromRoute] long id, [FromQuery] bool active)
        {
            try
            {
                var result = await service.UpdateSellerActiveStatus(id, active, Token);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
