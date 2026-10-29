#!/usr/bin/env python3
"""Generates src/OneAudio/Amr/AmrNbTables.g.cs and AmrWbTables.g.cs from the numeric tables of the 3GPP
reference C code that accompanies the specifications:

  TS 26.073 (AMR speech codec, ANSI-C code)       -> AmrNbTables.g.cs
  TS 26.173 (AMR-WB speech codec, ANSI-C code)    -> AmrWbTables.g.cs

Usage: tools/gen-amr-tables.py <unpacked TS 26.073 c-code dir> <unpacked TS 26.173 c-code dir> <output dir>

The tables are numeric data defined by the standards (codebooks, filter coefficients, bit allocations); the
generator only extracts the initialisers by name, evaluates their integer expressions and checks their sizes.
"""
import os
import re
import sys

nb_dir, wb_dir, out_dir = sys.argv[1:4]

# (C# name, file, C identifier). Identifier "name@n" selects the n-th definition of that name in the file.
NB = [
    ("Prmno", "bitno.tab", "prmno"), ("Prmnofsf", "bitno.tab", "prmnofsf"),
    ("Bitno475", "bitno.tab", "bitno_MR475"), ("Bitno515", "bitno.tab", "bitno_MR515"),
    ("Bitno59", "bitno.tab", "bitno_MR59"), ("Bitno67", "bitno.tab", "bitno_MR67"),
    ("Bitno74", "bitno.tab", "bitno_MR74"), ("Bitno795", "bitno.tab", "bitno_MR795"),
    ("Bitno102", "bitno.tab", "bitno_MR102"), ("Bitno122", "bitno.tab", "bitno_MR122"),
    ("BitnoDtx", "bitno.tab", "bitno_MRDTX"),
    ("Sort475", "bitno.tab", "sort_475"), ("Sort515", "bitno.tab", "sort_515"), ("Sort59", "bitno.tab", "sort_59"),
    ("Sort67", "bitno.tab", "sort_67"), ("Sort74", "bitno.tab", "sort_74"), ("Sort795", "bitno.tab", "sort_795"),
    ("Sort102", "bitno.tab", "sort_102"), ("Sort122", "bitno.tab", "sort_122"), ("SortSid", "bitno.tab", "sort_SID"),
    ("Dhf475", "d_homing.tab", "dhf_MR475"), ("Dhf515", "d_homing.tab", "dhf_MR515"),
    ("Dhf59", "d_homing.tab", "dhf_MR59"), ("Dhf67", "d_homing.tab", "dhf_MR67"),
    ("Dhf74", "d_homing.tab", "dhf_MR74"), ("Dhf795", "d_homing.tab", "dhf_MR795"),
    ("Dhf102", "d_homing.tab", "dhf_MR102"), ("Dhf122", "d_homing.tab", "dhf_MR122"),
    ("StartPos9", "c2_9pf.tab", "startPos"),
    ("Dgray", "gray.tab", "dgray"),
    ("QuaGainPitch", "gains.tab", "qua_gain_pitch"), ("QuaGainCode", "gains.tab", "qua_gain_code"),
    ("GainMr475", "qgain475.tab", "table_gain_MR475"),
    ("GainHighRates", "qua_gain.tab", "table_gain_highrates"), ("GainLowRates", "qua_gain.tab", "table_gain_lowrates"),
    ("InvSqrt", "inv_sqrt.tab", "table"), ("Log2", "log2.tab", "table"), ("Pow2", "pow2.tab", "table"),
    ("SqrtL", "sqrt_l.tab", "table"),
    ("LspTable", "lsp_lsf.tab", "table"), ("LspSlope", "lsp_lsf.tab", "slope"),
    ("LspInit", "lsp.tab", "lsp_init_data"),
    ("PhImpLowMr795", "ph_disp.tab", "ph_imp_low_MR795"), ("PhImpMidMr795", "ph_disp.tab", "ph_imp_mid_MR795"),
    ("PhImpLow", "ph_disp.tab", "ph_imp_low"), ("PhImpMid", "ph_disp.tab", "ph_imp_mid"),
    ("MeanLsf3", "q_plsf_3.tab", "mean_lsf"), ("MeanLsf5", "q_plsf_5.tab", "mean_lsf"), ("PredFac", "q_plsf_3.tab", "pred_fac"),
    ("PastRqInit", "q_plsf_3.tab", "past_rq_init"),
    ("Dico1Lsf3", "q_plsf_3.tab", "dico1_lsf"), ("Dico2Lsf3", "q_plsf_3.tab", "dico2_lsf"),
    ("Dico3Lsf3", "q_plsf_3.tab", "dico3_lsf"), ("Mr515Lsf3", "q_plsf_3.tab", "mr515_3_lsf"),
    ("Mr795Lsf1", "q_plsf_3.tab", "mr795_1_lsf"),
    ("Dico1Lsf5", "q_plsf_5.tab", "dico1_lsf"), ("Dico2Lsf5", "q_plsf_5.tab", "dico2_lsf"),
    ("Dico3Lsf5", "q_plsf_5.tab", "dico3_lsf"), ("Dico4Lsf5", "q_plsf_5.tab", "dico4_lsf"),
    ("Dico5Lsf5", "q_plsf_5.tab", "dico5_lsf"),
    ("Window200x40", "window.tab", "window_200_40"),
    ("Inter6", "pred_lt.c", "inter_6"),
    ("Gamma3Mr122", "pstfilt.c", "gamma3_MR122"), ("Gamma3", "pstfilt.c", "gamma3"),
    ("Gamma4Mr122", "pstfilt.c", "gamma4_MR122"), ("Gamma4", "pstfilt.c", "gamma4"),
]

WB = [
    ("Log2", "log2_tab.h", "table"), ("Isqrt", "math_op.c", "table_isqrt"), ("Pow2", "math_op.c", "table_pow2"),
    ("IspIsf", "isp_isf.tab", "table"), ("IspIsfSlope", "isp_isf.tab", "slope"),
    ("FirUp", "decim54.c", "fir_up"), ("FirDown", "decim54.c", "fir_down"),
    ("Inter4", "pred_lt4.c", "inter4_2"),
    ("PhImpLow", "ph_disp.c", "ph_imp_low"), ("PhImpMid", "ph_disp.c", "ph_imp_mid"),
    ("Fir6k7k", "hp6k.c", "fir_6k_7k"), ("Fir7k", "hp7k.c", "fir_7k"),
    ("QuaGain6b", "q_gain2.tab", "t_qua_gain6b"), ("QuaGain7b", "q_gain2.tab", "t_qua_gain7b"),
    ("MeanIsf", "qpisf_2s.tab", "mean_isf"), ("Dico1Isf", "qpisf_2s.tab", "dico1_isf"),
    ("Dico2Isf", "qpisf_2s.tab", "dico2_isf"), ("Dico21Isf", "qpisf_2s.tab", "dico21_isf"),
    ("Dico22Isf", "qpisf_2s.tab", "dico22_isf"), ("Dico23Isf", "qpisf_2s.tab", "dico23_isf"),
    ("Dico24Isf", "qpisf_2s.tab", "dico24_isf"), ("Dico25Isf", "qpisf_2s.tab", "dico25_isf"),
    ("Dico21Isf36", "qpisf_2s.tab", "dico21_isf_36b"), ("Dico22Isf36", "qpisf_2s.tab", "dico22_isf_36b"),
    ("Dico23Isf36", "qpisf_2s.tab", "dico23_isf_36b"),
    ("MeanIsfNoise", "qisf_ns.tab", "mean_isf_noise"), ("Dico1IsfNoise", "qisf_ns.tab", "dico1_isf_noise"),
    ("Dico2IsfNoise", "qisf_ns.tab", "dico2_isf_noise"), ("Dico3IsfNoise", "qisf_ns.tab", "dico3_isf_noise"),
    ("Dico4IsfNoise", "qisf_ns.tab", "dico4_isf_noise"), ("Dico5IsfNoise", "qisf_ns.tab", "dico5_isf_noise"),
    ("HpGain", "dec_main.c", "HP_gain"), ("IspInit", "dec_main.c", "isp_init"), ("IsfInit", "dec_main.c", "isf_init"),
    ("PdownUnusable", "d_gain2.c", "pdown_unusable"), ("CdownUnusable", "d_gain2.c", "cdown_unusable"),
    ("PdownUsable", "d_gain2.c", "pdown_usable"), ("CdownUsable", "d_gain2.c", "cdown_usable"),
    ("Prmnofsf", "homing.tab", "prmnofsf"),
    ("Dhf7k", "homing.tab", "dfh_M7k"), ("Dhf9k", "homing.tab", "dfh_M9k"), ("Dhf12k", "homing.tab", "dfh_M12k"),
    ("Dhf14k", "homing.tab", "dfh_M14k"), ("Dhf16k", "homing.tab", "dfh_M16k"), ("Dhf18k", "homing.tab", "dfh_M18k"),
    ("Dhf20k", "homing.tab", "dfh_M20k"), ("Dhf23k", "homing.tab", "dfh_M23k"), ("Dhf24k", "homing.tab", "dfh_M24k"),
    ("Sort660", "mime_io.tab", "sort_660"), ("Sort885", "mime_io.tab", "sort_885"), ("Sort1265", "mime_io.tab", "sort_1265"),
    ("Sort1425", "mime_io.tab", "sort_1425"), ("Sort1585", "mime_io.tab", "sort_1585"), ("Sort1825", "mime_io.tab", "sort_1825"),
    ("Sort1985", "mime_io.tab", "sort_1985"), ("Sort2305", "mime_io.tab", "sort_2305"), ("Sort2385", "mime_io.tab", "sort_2385"),
    ("SortSid", "mime_io.tab", "sort_SID"),
]


def strip_comments(s):
    s = re.sub(r"/\*.*?\*/", "", s, flags=re.S)
    return re.sub(r"//[^\n]*", "", s)


def find_array(path, ident):
    src = strip_comments(open(path, errors="replace").read())
    # Resolve simple object-like #defines used as array sizes or elements.
    defines = {}
    for m in re.finditer(r"^\s*#define\s+(\w+)\s+([^\s(][^\n]*|\([^\n]*\))\s*$", src, flags=re.M):
        defines[m.group(1)] = m.group(2).strip()
    pat = re.compile(r"(?:static\s+)?(?:const\s+)?(?:Word16|Word32|Word8|UWord8|Float)\s+\*?\s*" + re.escape(ident) +
                     r"\s*((?:\[[^\]]*\]\s*)+)=\s*\{(.*?)\}\s*;", re.S)
    m = pat.search(src)
    if not m:
        raise SystemExit(f"{path}: table {ident} not found")
    body = m.group(2)
    return body, defines


def eval_int(expr, defines):
    e = expr.strip()
    if not e:
        return None
    for _ in range(4):
        e = re.sub(r"\b[A-Za-z_]\w*\b", lambda mm: "(" + defines[mm.group(0)] + ")" if mm.group(0) in defines else mm.group(0), e)
    e = re.sub(r"\(\s*(?:Word16|Word32|short|int|long)\s*\)", "", e)
    e = re.sub(r"(?<=[0-9A-Fa-f])[Ll]\b", "", e)
    if not re.fullmatch(r"[0-9xXa-fA-F+\-*/%<>()\s]*", e):
        raise SystemExit(f"cannot evaluate '{expr}' -> '{e}'")
    v = eval(e.replace("/", "//"))
    return v


def extract(dirname, fname, ident):
    body, defines = find_array(os.path.join(dirname, fname), ident)
    items = []
    # split on commas not inside parentheses
    depth = 0
    cur = ""
    for ch in body:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        if ch == "," and depth == 0:
            items.append(cur)
            cur = ""
        else:
            cur += ch
    items.append(cur)
    vals = [eval_int(i, defines) for i in items]
    return [v for v in vals if v is not None]


def emit(path, ns_class, entries, header):
    lines = [
        "// <auto-generated>",
        f"// Produced by tools/gen-amr-tables.py from the numeric tables of {header}.",
        "// Do not edit by hand.",
        "// </auto-generated>",
        "namespace OneAudio.Amr;",
        "",
        f"internal static class {ns_class}",
        "{",
    ]
    for name, vals in entries:
        lines.append(f"    public static readonly short[] {name} = [{', '.join(str(v) for v in vals)}];")
    lines.append("}")
    open(path, "w").write("\n".join(lines) + "\n")


def build(dirname, spec):
    entries = []
    for name, fname, ident in spec:
        vals = extract(dirname, fname, ident)
        for v in vals:
            if not -32768 <= v <= 65535:
                raise SystemExit(f"{name}: value {v} out of 16-bit range")
        entries.append((name, [v if v < 32768 else v - 65536 for v in vals]))
    return entries


os.makedirs(out_dir, exist_ok=True)
emit(os.path.join(out_dir, "AmrNbTables.g.cs"), "AmrNbTables", build(nb_dir, NB),
     "the 3GPP AMR speech codec (TS 26.073 ANSI-C code, accompanying TS 26.090/26.101)")
wb_entries = build(wb_dir, WB)
emit(os.path.join(out_dir, "AmrWbTables.g.cs"), "AmrWbTables", wb_entries,
     "the 3GPP AMR-WB speech codec (TS 26.173 ANSI-C code, accompanying TS 26.190/26.201)")
print({n: len(v) for n, v in wb_entries})
