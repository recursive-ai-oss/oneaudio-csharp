/* Test-only oracle: decodes an Ogg Vorbis file with libvorbisfile's documented public API (ov_fopen,
 * ov_read_float, ov_info, ov_pcm_total) and writes
 *   <out>.f32  interleaved little-endian float32, links concatenated, each link with its own channel count
 *   <out>.txt  one line per link: "link channels rate frames" (frames counted from what was actually read),
 *              then "total <ov_pcm_total(-1)>" and "errors <count of negative read returns>".
 * The vorbisfile headers are not needed: the few structure layouts used here are declared by hand from the
 * published API reference, and OggVorbis_File is treated as an opaque, generously sized, aligned buffer.
 * Build: cc -O2 -o vorbis_ref_float vorbis_ref_float.c /usr/lib/x86_64-linux-gnu/libvorbisfile.so.3
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { int version; int channels; long rate; long bitrate_upper, bitrate_nominal, bitrate_lower, bitrate_window; void *codec_setup; } vorbis_info;
typedef struct { _Alignas(16) unsigned char opaque[8192]; } OggVorbis_File;

extern int ov_fopen(const char *path, OggVorbis_File *vf);
extern int ov_clear(OggVorbis_File *vf);
extern int ov_streams(OggVorbis_File *vf);
extern long long ov_pcm_total(OggVorbis_File *vf, int i);
extern vorbis_info *ov_info(OggVorbis_File *vf, int link);
extern long ov_read_float(OggVorbis_File *vf, float ***pcm_channels, int samples, int *bitstream);

int main(int argc, char **argv)
{
    if (argc != 3) { fprintf(stderr, "usage: %s in.ogg out-prefix\n", argv[0]); return 2; }
    static OggVorbis_File vf;
    int rc = ov_fopen(argv[1], &vf);
    if (rc != 0) { fprintf(stderr, "ov_fopen failed: %d\n", rc); return 3; }
    char name[4096];
    snprintf(name, sizeof name, "%s.f32", argv[2]);
    FILE *out = fopen(name, "wb");
    snprintf(name, sizeof name, "%s.txt", argv[2]);
    FILE *txt = fopen(name, "w");
    if (!out || !txt) return 4;

    int streams = ov_streams(&vf);
    long long *frames = calloc((size_t)streams, sizeof *frames);
    long errors = 0;
    float *inter = NULL; size_t cap = 0;
    for (;;) {
        float **pcm; int link = 0;
        long n = ov_read_float(&vf, &pcm, 4096, &link);
        if (n == 0) break;
        if (n < 0) { errors++; if (errors > 1000) break; continue; }
        int ch = ov_info(&vf, link)->channels;
        if ((size_t)(n * ch) > cap) { cap = (size_t)(n * ch); inter = realloc(inter, cap * sizeof *inter); }
        for (long i = 0; i < n; i++) for (int c = 0; c < ch; c++) inter[i * ch + c] = pcm[c][i];
        fwrite(inter, sizeof(float), (size_t)(n * ch), out);
        frames[link] += n;
    }
    for (int l = 0; l < streams; l++) {
        vorbis_info *vi = ov_info(&vf, l);
        fprintf(txt, "%d %d %ld %lld\n", l, vi->channels, vi->rate, frames[l]);
    }
    fprintf(txt, "total %lld\nerrors %ld\n", ov_pcm_total(&vf, -1), errors);
    fclose(out); fclose(txt);
    ov_clear(&vf);
    return 0;
}
