using Comgenie.Util;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static Comgenie.Server.Handlers.Http.HttpHandler;

namespace Comgenie.Server.Handlers.Http
{
    public partial class HttpHandler
    {
        public void AddApplicationRoute(string domain, string path, object httpApplication, bool lowerCaseMethods = true, bool allPublicMethods = false)
        {
            // Add all methods of the given httpApplication class as seperate routes. The 'Other' method will be used if no suitable methods are found for a request.
            var publicMethods = httpApplication.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var ignoreMethods = typeof(object).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(a => a.Name).ToArray();
            
            foreach (var method in publicMethods)
            {
                if (allPublicMethods && (ignoreMethods.Contains(method.Name) || method.ReturnType == typeof(void)))
                    continue;
                if (!allPublicMethods && method.ReturnType != typeof(HttpResponse) && method.ReturnType != typeof(Task<HttpResponse>) && method.ReturnType != typeof(Task<HttpResponse?>))
                    continue;

                var methodParameters = method.GetParameters();

                AddRoute(domain, path + (method.Name == "Index" ? "" : method.Name == "Other" ? "/*" : "/" + (lowerCaseMethods ? method.Name.ToLower() : method.Name)), new Route()
                {
                    HandleExecuteRequestAsync = async (client, data) => {
                        if (data.Request == null)
                            return null;
                        // Parse arguments
                        Dictionary<string, string> rawParameters = new Dictionary<string, string>();
                        var parameterStart = data.Request.IndexOf("?");
                        if (parameterStart > 0)
                        {
                            // GET parameters
                            GetParametersFromQueryString(rawParameters, data.Request.Substring(parameterStart + 1));
                        }

                        if (data.DataStream != null && data.DataStream.Length > 0)
                        {
                            // POST parameters                                
                            if (data.ContentType != null && data.ContentType.StartsWith("application/json") && data.DataLength < 1024 * 1024 * 100)
                            {
                                // Parse as json
                                try
                                {
                                    var items = JsonSerializer.Deserialize<Dictionary<string, object>>(data.DataStream);
                                    if (items != null)
                                    {
                                        foreach (var item in items)
                                        {
                                            if (item.Value != null)
                                                rawParameters.Add(item.Key, item.Value.ToString()!);
                                        }
                                    }
                                }
                                catch { }
                            }
                            else if (data.ContentType == "application/x-www-form-urlencoded" && data.DataLength < 1024 * 1024 * 100)
                            {
                                // Parse as normal=query&string=parameters
                                using (var sr = new StreamReader(data.DataStream, Encoding.UTF8, leaveOpen: true))
                                    GetParametersFromQueryString(rawParameters, await sr.ReadToEndAsync());
                            }
                            else if (data.ContentType != null && data.ContentType.StartsWith("multipart/form-data; boundary=")) // Usually a file upload, but can be form data as well
                            {
                                data.FileData = new List<HttpClientFileData>();
                                var boundary = data.ContentType.Substring(30);
                                if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                                    boundary = boundary.Trim('"');
                                var boundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);
                                long curDataPos = 0;
                                long startContent = -1;

                                // TODO: Optimize

                                for (; curDataPos < data.DataLength - boundaryBytes.Length; curDataPos++)
                                {
                                    data.DataStream.Position = curDataPos;

                                    /// Find the boundary 
                                    var found = true;
                                    for (var j = 0; j < boundaryBytes.Length; j++)
                                    {
                                        var curChar = (byte)data.DataStream.ReadByte();
                                        if (curChar != boundaryBytes[j])
                                        {
                                            found = false;
                                            break;
                                        }
                                    }

                                    if (found)
                                    {
                                        if (startContent < 0)
                                        {
                                            // Found the first boundary
                                            startContent = curDataPos + boundaryBytes.Length + 2;  // The start boundary is followed by \r\n
                                            continue;
                                        }

                                        // Found the last boundary
                                        long endContent = curDataPos - 2;

                                        /// Handle content                                    
                                        // Parse headers
                                        var headers = new Dictionary<string, string>();
                                        var headerName = "";
                                        var curLineStart = startContent;
                                        var curValueStart = startContent;

                                        data.DataStream.Position = startContent;
                                        var last3Bytes = new byte[3];
                                        var curData = "";
                                        for (var i = startContent; i < endContent; i++)
                                        {
                                            last3Bytes[0] = last3Bytes[1];
                                            last3Bytes[1] = last3Bytes[2];
                                            last3Bytes[2] = (byte)data.DataStream.ReadByte();

                                            if (i >= 2 && last3Bytes[2] == '\n' && last3Bytes[0] == '\n')
                                            {
                                                // Found the end of the header
                                                startContent = i + 1;
                                                break;
                                            }
                                            else if (headerName == "" && last3Bytes[2] == ':')
                                            {
                                                // Got a header name
                                                headerName = curData;
                                                curData = "";
                                                curValueStart = i + 2;
                                                i++; // We can ignore the next character (space)
                                                data.DataStream.ReadByte();
                                            }
                                            else if (last3Bytes[2] == '\r' && headerName != "")
                                            {
                                                // Got a header value

                                                var headerValue = curData;
                                                curData = "";
                                                if (!headers.ContainsKey(headerName.ToLower()))
                                                    headers.Add(headerName.ToLower(), headerValue);
                                                headerName = "";
                                                curLineStart = i + 2;

                                                //i++; // We can ignore the next character (\n)
                                                //data.DataStream.ReadByte();
                                            }
                                            else if (last3Bytes[2] != '\r' && last3Bytes[2] != '\n')
                                            {
                                                curData += Convert.ToChar(last3Bytes[2]);
                                            }
                                        }

                                        if (headers.Count > 0)
                                        {
                                            var skipFileData = false;
                                            string? fileName = null;
                                            // See if this is a form element, or a file upload
                                            if (headers.ContainsKey("content-disposition"))
                                            {
                                                var headerValue = headers["content-disposition"];
                                                var formFieldName = headerValue.Between("name=\"", "\"");
                                                if (formFieldName != null)
                                                {
                                                    if (headerValue.Contains("filename="))
                                                    {
                                                        // File upload
                                                        var headerValueFileName = headerValue.Substring(headerValue.IndexOf("filename=") + 9);
                                                        if (headerValueFileName.Contains(";"))
                                                            headerValueFileName = headerValueFileName.Substring(0, headerValueFileName.IndexOf(";"));
                                                        fileName = headerValueFileName.Replace("\"", "").Trim();
                                                    }
                                                    else
                                                    {
                                                        // Form data                                                    
                                                        data.DataStream.Position = startContent;
                                                        var dataBlock = new byte[endContent - startContent];
                                                        var dataLen = 0;
                                                        while (dataLen < dataBlock.Length)
                                                        {
                                                            var tmpDataLen = data.DataStream.Read(dataBlock);
                                                            if (tmpDataLen <= 0)
                                                                throw new Exception("Invalid posted data");
                                                            dataLen += tmpDataLen;
                                                        }

                                                        rawParameters.Add(formFieldName, Encoding.UTF8.GetString(dataBlock));
                                                        skipFileData = true;
                                                    }
                                                }
                                            }

                                            if (!skipFileData)
                                            {
                                                data.FileData.Add(new HttpClientFileData(fileName, startContent, endContent - startContent, headers, data.DataStream));
                                            }
                                        }

                                        // Prepare for next content
                                        startContent = endContent + 2 + boundaryBytes.Length + 2; // Next content is after \r\n, the end boundary, and another \r\n
                                    }
                                }
                            }

                            data.DataStream.Position = 0;
                        }

                        var paramValues = new List<object?>();
                        foreach (var param in methodParameters)
                        {
                            if (param.ParameterType == typeof(HttpClientData))
                                paramValues.Add(data);
                            else if (param.Name != null && rawParameters.ContainsKey(param.Name))
                            {
                                if (param.ParameterType == typeof(string))
                                    paramValues.Add(rawParameters[param.Name]);
                                else if (param.ParameterType == typeof(int))
                                    paramValues.Add(int.Parse(rawParameters[param.Name]));
                                else if (param.ParameterType == typeof(bool))
                                    paramValues.Add(bool.Parse(rawParameters[param.Name]));
                                else if (param.ParameterType == typeof(double))
                                    paramValues.Add(double.Parse(rawParameters[param.Name], CultureInfo.InvariantCulture));
                                else if (param.ParameterType == typeof(float))
                                    paramValues.Add((float)double.Parse(rawParameters[param.Name], CultureInfo.InvariantCulture));
                                else if (rawParameters[param.Name].Length > 0 && (rawParameters[param.Name].StartsWith("{") || rawParameters[param.Name].StartsWith("[")))
                                {
                                    // Complex object posted in JSON, try to deserialize
                                    try
                                    {
                                        var obj = JsonSerializer.Deserialize(rawParameters[param.Name], param.ParameterType);
                                        paramValues.Add(obj);
                                    }
                                    catch
                                    {
                                        paramValues.Add(null); // Error while deserializing 
                                    }
                                }
                                else
                                    paramValues.Add(null); // Unsupported
                            }
                            else if (param.HasDefaultValue)
                                paramValues.Add(param.DefaultValue);
                            else
                                paramValues.Add(null);
                        }
                        var responseObj = method.Invoke(httpApplication, paramValues.ToArray());
                        if (responseObj is Task)
                        {
                            await Task.WhenAll((Task)responseObj);
                            if (responseObj is Task<HttpResponse>)
                                responseObj = ((Task<HttpResponse>)responseObj).Result;
                            else if (responseObj is Task<HttpResponse?>)
                                responseObj = ((Task<HttpResponse?>)responseObj).Result;
                            else
                            {
                                var resultProperty = ((Task)responseObj).GetType().GetProperty("Result"); // TODO: See if we can skip this reflection step for better performance
                                if (resultProperty != null)
                                    responseObj = resultProperty.GetValue(responseObj);
                                else
                                    responseObj = null;
                            }
                        }

                        if (responseObj is HttpResponse)
                        {
                            return (HttpResponse?)responseObj;
                        }
                        else if (responseObj != null)
                        {
                            return new HttpResponse()
                            {
                                ResponseObject = responseObj
                            };
                        }
                        return null;
                    }
                });
            }
        }
    }
}
