## Comgenie.Storage
A way to set up a storage pool with one or multiple storage locations (Disk, Azure, Anything custom). The library will encrypt and add repair data when storing files, and will automatically repair files when reading them. Note that this library does not create a virtual disk, it just offers a way to open and manipulate files using code.

- Encryption using AES256
- Repair data using Reed Solomon
- Support for priority and failover of storage locations
- Assign and search for files using tags
- Support for custom storage locations
- Sync to a shared storage location (can be accessed by multiple instances of the application)
- 

To get started, please take a look at the examples provided at https://github.com/Comgenie/ComgenieLibraries