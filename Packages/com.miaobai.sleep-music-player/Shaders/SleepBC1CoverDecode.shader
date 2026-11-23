// GPU BC1 (DXT1) 封面图集解码着色器：将后端封装的 PNG 字节流在 GPU 单 Pass 解码为标准 RGB 纹理。
Shader "SleepSanctuary/GPU/BC1CoverDecode"
{
    Properties
    {
        _MainTex ("BC1 数据图集", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            // 按字节索引精确读取图集像素（已做线性/sRGB 空间还原）
            float3 ReadBytes(float index)
            {
                float w = _MainTex_TexelSize.z;
                float h = _MainTex_TexelSize.w;
                float2 uv = float2((fmod(index, w) + 0.5) / w, 1.0 - (floor(index / w) + 0.5) / h);
                float3 rgb = tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                rgb = lerp(rgb * 12.92, 1.055 * pow(max(rgb, 0.0), 1.0 / 2.4) - 0.055, step(0.0031308, rgb));
                #endif
                return floor(saturate(rgb) * 255.0 + 0.5);
            }

            // 将 16 位 RGB565 端点颜色解码为 0..1 浮点 RGB
            float3 DecodeRGB565(float n)
            {
                return float3(
                    floor(n / 2048.0) / 31.0,
                    fmod(floor(n / 32.0), 64.0) / 63.0,
                    fmod(n, 32.0) / 31.0
                );
            }

            float4 frag(v2f_img i) : SV_Target
            {
                // 校验 wimbc1png1 协议头与原始尺寸
                float3 h0 = ReadBytes(0);
                float3 h1 = ReadBytes(1);
                float2 size = float2(h0.r + h0.g * 256.0, h0.b + h1.r * 256.0);
                if (h1.g != 87.0 || h1.b != 1.0 || min(size.x, size.y) < 1.0 || max(size.x, size.y) > 4096.0)
                    return float4(0, 0, 0, 0);

                // 定位当前像素所属的 4x4 BC1 压缩块
                float2 p = min(floor(float2(i.uv.x, 1.0 - i.uv.y) * size), size - 1.0);
                float blocksPerRow = ceil(size.x / 4.0);
                float blockIdx = floor(p.y / 4.0) * blocksPerRow + floor(p.x / 4.0);
                float byteIdx = 2.0 + blockIdx * 3.0;
                if (byteIdx + 2.0 >= _MainTex_TexelSize.z * _MainTex_TexelSize.w)
                    return float4(0, 0, 0, 0);

                // 读取两个端点色与 2-bit 调色板索引
                float3 a = ReadBytes(byteIdx);
                float3 b = ReadBytes(byteIdx + 1.0);
                float3 c = ReadBytes(byteIdx + 2.0);
                float c0 = a.r + a.g * 256.0;
                float c1 = a.b + b.r * 256.0;

                float row = fmod(p.y, 4.0);
                float bits = row < 0.5 ? b.g : (row < 1.5 ? b.b : (row < 2.5 ? c.r : c.g));
                float pal = fmod(floor(bits / exp2(fmod(p.x, 4.0) * 2.0)), 4.0);

                float3 col0 = DecodeRGB565(c0);
                float3 col1 = DecodeRGB565(c1);
                float3 rgb = pal < 0.5 ? col0 : (pal < 1.5 ? col1 : (pal < 2.5 ? (col0 * 2.0 + col1) / 3.0 : (col0 + col1 * 2.0) / 3.0));

                #ifndef UNITY_COLORSPACE_GAMMA
                rgb = GammaToLinearSpace(rgb);
                #endif
                return float4(rgb, 1.0);
            }
            ENDCG
        }
    }
}
