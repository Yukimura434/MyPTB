# Native camera runtimes

This directory contains vendor runtime binaries required by the PhotoBooth camera adapters.
The binaries are copied to the Admin and Customer output directories by their project files.

- `Canon/EDSDK.dll`: Canon EDSDK runtime.
- `Canon/EdsImage.dll`: Canon image runtime used by EDSDK.

Do not expose these files through the Worker or web frontend. Review vendor redistribution terms before shipping installers.
