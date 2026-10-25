These small videos are generated from FFmpeg's `testsrc2` pattern; they contain no external media.

The fractional-frame-rate pair reproduces timestamp rounding between an H.264 source and an AV1 WebM comparison clip. Both have 24000/1001 fps; WebM stores timestamps in milliseconds. The lossless clip allows pixel comparisons in addition to frame-index checks.

```sh
ffmpeg -f lavfi -i 'testsrc2=size=64x48:rate=24000/1001:duration=4' -c:v libx264 -preset medium -g 48 -bf 3 -pix_fmt yuv420p comparison-23976.mp4
ffmpeg -ss 1.116 -i comparison-23976.mp4 -t 2 -c:v libaom-av1 -cpu-used 8 -crf 0 -b:v 0 -pix_fmt yuv420p comparison-23976-trim.webm
```
