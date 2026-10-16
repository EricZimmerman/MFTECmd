# DFIR Artifact Museum samples

From https://github.com/AndrewRathbun/DFIRArtifactMuseum/tree/main/Windows/NTFSArtifacts (MIT license), all from the
same system (`Win2012R2/StolenSzechuan`):

- `$J.gz`: gzipped `J/Win2012R2/StolenSzechuan/$J` (9.2 MB -> 0.9 MB)
- `$LogFile.gz`: gzipped `LogFile/Win2012R2/StolenSzechuan/$LogFile` (18 MB -> 1.6 MB)
- `$MFT_directories.gz`: `MFT/Win2012R2/StolenSzechuan/$MFT` with every FILE record except the first 16 and the
  directories zeroed (only directories are parents, so $J paths come out the same as with the whole $MFT), gzipped
  (89 MB -> 2.4 MB)
- `$Boot` and `$Secure_$SDS`: as they are

The tests decompress the `.gz` files. Expected $J paths match usnjrnl_rewind (CyberCX, MIT) run on MFTECmd's CSVs of
the whole $MFT and $J.
