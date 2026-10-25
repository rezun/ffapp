These small videos are generated from FFmpeg's `testsrc2` pattern; they contain no external media.

The fractional-frame-rate pair reproduces timestamp rounding between an H.264 source and an AV1 WebM comparison clip. Both have 24000/1001 fps; WebM stores timestamps in milliseconds. The lossless clip allows pixel comparisons in addition to frame-index checks.

```sh
ffmpeg -f lavfi -i 'testsrc2=size=64x48:rate=24000/1001:duration=4' -c:v libx264 -preset medium -g 48 -bf 3 -pix_fmt yuv420p comparison-23976.mp4
ffmpeg -ss 1.116 -i comparison-23976.mp4 -t 2 -c:v libaom-av1 -cpu-used 8 -crf 0 -b:v 0 -pix_fmt yuv420p comparison-23976-trim.webm
```

`comparison-hdr10.mp4` is a lossless 10-bit HDR10 clip (PQ, BT.2020) used to check HDR→SDR tone mapping. The left half is BT.709 red and the right half 50% gray, so wrong primaries or a missing PQ conversion are visible as desaturated red. It carries HDR10 mastering and MaxCLL metadata like real releases, which catches metadata leaking into the SDR conversion.

```sh
ffmpeg -f lavfi -i 'color=c=red:size=32x48:rate=25:duration=0.2' -f lavfi -i 'color=c=0x808080:size=32x48:rate=25:duration=0.2' -filter_complex '[0][1]hstack,format=gbrpf32le,zscale=tin=bt709:pin=bt709:rin=full:t=smpte2084:p=bt2020:m=bt2020nc:r=tv:npl=100,format=yuv420p10le' -c:v libx265 -x265-params 'lossless=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400' -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv -tag:v hvc1 comparison-hdr10.mp4
```
