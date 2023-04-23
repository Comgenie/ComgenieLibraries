using Comgenie.Server.Handlers;
using System.Text;

namespace HttpServerExample
{

    internal class App
    {
        // /app
        public HttpHandler.HttpResponse Index(HttpHandler.HttpClientData httpClientData)
        {
            return new HttpHandler.HttpResponse()
            {
                StatusCode = 200,
                Data = Encoding.UTF8.GetBytes("Hi welcome at /app !")
            };
        }

        // /app/ReverseText
        public HttpHandler.HttpResponse ReverseText(HttpHandler.HttpClientData httpClientData, string text = "default value")
        {
            return new HttpHandler.HttpResponse()
            {
                StatusCode = 200,
                ContentType = "text/plain",
                Data = Encoding.UTF8.GetBytes(text.Reverse().ToArray())
            };
        }

        // /app/TimesTwo
        public HttpHandler.HttpResponse TimesTwo(HttpHandler.HttpClientData httpClientData, ExampleDTO dto)
        {
            if (dto == null)
                return new HttpHandler.HttpResponse(400, "Missing object");

            dto.Number *= 2;
            return new HttpHandler.HttpResponse(200, dto);
        }

        // /app/AllOtherMethods
        public HttpHandler.HttpResponse Other(HttpHandler.HttpClientData httpClientData)
        {
            return new HttpHandler.HttpResponse()
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