using Comgenie.Server.Handlers.Http;
using System.Text;

namespace HttpServerExample
{

    internal class App
    {
        // /app
        public HttpResponse Index(HttpClientData httpClientData)
        {
            return new HttpResponse()
            {
                StatusCode = 200,
                Data = Encoding.UTF8.GetBytes("Hi welcome at /app !")
            };
        }

        // /app/ReverseText
        public HttpResponse ReverseText(HttpClientData httpClientData, string text = "default value")
        {
            return new HttpResponse()
            {
                StatusCode = 200,
                ContentType = "text/plain",
                Data = Encoding.UTF8.GetBytes(text.Reverse().ToArray())
            };
        }

        // /app/TimesTwo
        public async Task<HttpResponse> TimesTwo(HttpClientData httpClientData, ExampleDTO dto)
        {
            if (dto == null)
                return new HttpResponse(400, "Missing object");

            await Task.Delay(1000); // Example delay to demonstrate async abilities

            dto.Number *= 2;
            return new HttpResponse(200, dto);
        }

        // /app/AllOtherMethods
        public HttpResponse Other(HttpClientData httpClientData)
        {
            return new HttpResponse()
            {
                StatusCode = 200,
                Data = Encoding.UTF8.GetBytes("Gonna catch them all")
            };
        }

        public class ExampleDTO
        {
            public int Number { get; set; }
        }
    }

}