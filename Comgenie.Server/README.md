## Comgenie.Server
This library gives the ability to run http and smtp servers from your own code, including automatic valid SSL for both (using LetsEncrypt). The library should be easy to expand with other tcp servers as well. 

The http server has the following features:

- File routes (with GZip compression)
- Application routes similar to controllers in ASP.Net MVC
- Reverse proxy including response manipulation
- Support for hosting a second instance remotely and adding routes/handling requests of the main instance
- Websockets
- Abstract WebDAV class to easily create a custom WebDAV server

The smtp server has the following features:

- DKIM verification
- Utility to send DKIM signed email
- StartTLS


To get started, please take a look at the examples provided at https://github.com/Comgenie/ComgenieLibraries