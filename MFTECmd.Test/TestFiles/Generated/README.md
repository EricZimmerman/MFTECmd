# Generated test files

Made on Linux with ntfsprogs, so they have no third-party content:

```bash
truncate -s 64M vol.img
mkntfs -F -f -q -L TestVol -c 4096 vol.img
echo "hello world" > hello.txt; head -c 1000 /dev/urandom > a.bin
ntfscp vol.img hello.txt hello.txt
ntfscp vol.img a.bin a.bin
for i in $(seq 1 30); do echo "file $i" > f$i.txt; ntfscp vol.img f$i.txt "Long file name number $i.txt"; done
ntfscat vol.img '$Boot' > '$Boot'
ntfscat vol.img '$MFT' > '$MFT'
ntfscat -a 0xA0 -n '$I30' vol.img / > '$I30'          # the root directory's index allocation
ntfscat -a 0x80 -n '$SDS' vol.img '$Secure' > '$SDS'
```

Expected values in the tests come from `ntfsinfo` and `ntfsls` on the image (cluster size 4096, MFT at cluster 4, mirror at 8191, volume serial 330892F2154739CE).
