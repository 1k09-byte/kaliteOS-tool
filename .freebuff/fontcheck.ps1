Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;
public class Ttf {
  static ushort R16(byte[] b, int o) { return (ushort)((b[o] << 8) | b[o + 1]); }
  static uint R32(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }
  public static string Family(string path) {
    byte[] b = File.ReadAllBytes(path);
    ushort numTables = R16(b, 4);
    int nameOff = -1, nameLen = 0;
    for (int i = 0; i < numTables; i++) {
      int o = 12 + i * 16;
      string tag = Encoding.ASCII.GetString(b, o, 4);
      if (tag == "name") { nameOff = (int)R32(b, o + 8); nameLen = (int)R32(b, o + 12); }
    }
    if (nameOff < 0) return "NO name table";
    ushort count = R16(b, nameOff + 2);
    ushort strOff = R16(b, nameOff + 4);
    string best = "?";
    for (int i = 0; i < count; i++) {
      int o = nameOff + 6 + i * 12;
      ushort pid = R16(b, o), eid = R16(b, o + 2), lid = R16(b, o + 4);
      ushort nid = R16(b, o + 6);
      if (nid != 1) continue;
      ushort len = R16(b, o + 8), so = R16(b, o + 10);
      string s;
      if (pid == 3 || pid == 0) {
        byte[] raw = new byte[len];
        Array.Copy(b, nameOff + strOff + so, raw, 0, len);
        s = Encoding.BigEndianUnicode.GetString(raw);
      } else {
        s = Encoding.ASCII.GetString(b, nameOff + strOff + so, len);
      }
      if (pid == 3 && eid == 1 && lid == 0x409) return s;
      best = s;
    }
    return best;
  }
  public static string Ascii(string path) {
    byte[] b = File.ReadAllBytes(path);
    ushort numTables = R16(b, 4);
    int cmapOff = -1;
    for (int i = 0; i < numTables; i++) {
      int o = 12 + i * 16;
      if (Encoding.ASCII.GetString(b, o, 4) == "cmap") cmapOff = (int)R32(b, o + 8);
    }
    if (cmapOff < 0) return "NO cmap";
    ushort nSub = R16(b, cmapOff + 2);
    int best = -1;
    for (int i = 0; i < nSub; i++) {
      int o = cmapOff + 4 + i * 8;
      ushort pid = R16(b, o), eid = R16(b, o + 2);
      int off = cmapOff + (int)R32(b, o + 4);
      if ((pid == 3 && (eid == 1 || eid == 10)) || (pid == 0)) {
        if (R16(b, off) == 4 || R16(b, off) == 12) best = off;
        if (R16(b, off) == 4 && eid == 1) { best = off; break; }
      }
    }
    if (best < 0) return "no usable subtable";
    ushort fmt = R16(b, best);
    int missing = 0;
    if (fmt == 4) {
      ushort segX2 = R16(b, best + 6);
      int seg = segX2 / 2;
      int endO = best + 14;
      int startO = endO + segX2 + 2;
      for (int c = 32; c <= 126; c++) {
        bool found = false;
        for (int s = 0; s < seg; s++) {
          ushort en = R16(b, endO + s * 2);
          ushort st = R16(b, startO + s * 2);
          if (c >= st && c <= en) { found = true; break; }
        }
        if (!found) missing++;
      }
    } else {
      return "fmt " + fmt + " (not checked)";
    }
    return missing == 0 ? "ASCII 32-126 OK" : ("MISSING " + missing);
  }
}
"@
$d = "src/kaliteConfig/Assets/Fonts"
Get-ChildItem $d -Filter *.ttf | ForEach-Object {
  $fam = [Ttf]::Family($_.FullName)
  $asc = [Ttf]::Ascii($_.FullName)
  "{0,-32} family='{1}'  {2}" -f $_.Name, $fam, $asc
}
