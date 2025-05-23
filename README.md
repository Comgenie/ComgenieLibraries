# Comgenie Libraries
Some useful libraries and utilities written in .net core without any external dependencies, can be used cross platform. 
This library can be freely used within any of your projects, as long as there are credits included within the application interface (visible to the end user) to https://comgenie.com

## Comgenie.Server
This library gives the ability to run http and smtp servers from your own code, including automatic valid SSL for both (using LetsEncrypt). The library should be easy to expand with other tcp servers as well. 
Available as [nuget package](https://www.nuget.org/packages/Comgenie.Server/) .

The http server has the following features:

- File routes (with GZip compression)
- Application routes similar to controllers in ASP.Net MVC
- Reverse proxy including response manipulation
- Support for hosting a second instance remotely and adding routes/handling requests of the main instance
- Websockets
- Abstract WebDAV class to easily create a custom WebDAV server
- See the HttpServerExample project for examples for most of the features listed above

The smtp server has the following features:

- DKIM verification
- Utility to send DKIM signed email
- StartTLS


## Comgenie.Storage
A way to set up a storage pool with one or multiple storage locations (Disk, Azure, Anything custom). The library will encrypt and add repair data when storing files, and will automatically repair files when reading them. Note that this library does not create a virtual disk, it just offers a way to open and manipulate files using code.
Available as [nuget package](https://www.nuget.org/packages/Comgenie.Storage/) .

- Encryption using AES256
- Repair data using Reed Solomon
- Support for priority and failover of storage locations
- Assign and search for files using tags
- Support for custom storage locations
- Sync to a shared storage location (can be accessed by multiple instances of the application)
- See the StorageExample project for examples for most of the features listed above

## Comgenie.Util
Collection of useful utilities (used by both Comgenie.Server and Server.Storage)
Available as [nuget package](https://www.nuget.org/packages/Comgenie.Util/) .

- The EncryptedAndRepairableStream adds encryption and repair data to any stream
- CallbackStream calls custom actions for each method executed on the stream
- SubStream can create a smaller stream within a larger stream
- ForwardStream to move data between two calls which don't provide any stream themselves but only consumes them.
- SuperTree for a fast and memory efficient tag searcher, with wildcard support
- QueryTranslator turns a linq-expression into a simple parsable text filter