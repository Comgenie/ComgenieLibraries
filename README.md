# Comgenie Libraries
Some useful libraries and utilities written in .net core without any external dependencies, can be used cross platform. 
This library can be freely used within any of your projects, as long as there are credits included within the application interface (visible to the end user) to https://comgenie.com

## Comgenie.Server
This library gives the ability to run http and smtp servers from your own code, including automatic valid SSL for both (using LetsEncrypt). The library should be easy to expand with other tcp servers as well. 

The http server has the following features:

- File routes (with GZip compression)
- Application routes similar to controllers in ASP.Net MVC
- Reverse proxy including response manipulation
- Support for hosting a second instance remotely and adding routes/handling requests of the main instance

The smtp server has the following features:

- DKIM and SPF verification
- Utility to send DKIM signed email
- StartTLS
