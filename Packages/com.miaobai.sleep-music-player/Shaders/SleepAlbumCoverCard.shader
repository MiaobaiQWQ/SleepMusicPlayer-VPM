// 左侧专辑封面卡片着色器：强制采样 Mip 0 保持原图锐利，并绘制 SDF 圆角、光泽与外阴影。
Shader "SleepSanctuary/GPU/AlbumCoverCard"
{
    Properties
    {
        _MainTex ("解码封面纹理", 2D) = "black" {}
        _HasCover ("封面淡入权重", Range(0, 1)) = 0
        _CornerRadius ("圆角半径", Range(0.02, 0.2)) = 0.085
        _RimColor ("暖金细描边", Color) = (0.95, 0.74, 0.48, 0.55)
    }

    SubShader
    {
        Tags { "Queue"="Transparent+1" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float _HasCover;
            float _CornerRadius;
            float4 _RimColor;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // GPU SDF 圆角矩形遮罩
                float2 q = abs(uv - 0.5) - (0.48 - _CornerRadius);
                float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - _CornerRadius;
                float mask = smoothstep(0.006, -0.006, d);

                // 无封面时程序化绘制黑胶唱片占位图
                float dist = length(uv - 0.5);
                float rings = 0.5 + 0.5 * sin(dist * 110.0);
                float disc = smoothstep(0.39, 0.37, dist);
                float label = smoothstep(0.14, 0.125, dist);
                float hole = smoothstep(0.028, 0.022, dist);
                float3 idle = float3(0.09, 0.095, 0.115) + disc * (0.04 + 0.02 * rings) + label * float3(0.32, 0.22, 0.13);
                idle *= (1.0 - hole * 0.85);

                // 显式采样 Mip 0，确保封面文字与细节 100% 锐利
                float3 art = tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)).rgb;
                float3 col = lerp(idle, art, _HasCover);

                // 轻柔斜向玻璃反光与暖金细边
                col += smoothstep(0.45, 0.0, abs((uv.x + uv.y) - 1.25)) * 0.07;
                col = lerp(col, _RimColor.rgb, smoothstep(0.009, 0.0, abs(d)) * _RimColor.a);

                // 柔和外投影
                float shadow = smoothstep(0.04, 0.0, max(d, 0.0)) * 0.45 * (1.0 - mask);
                col = lerp(float3(0.01, 0.01, 0.015), col, mask);

                return fixed4(col, saturate(mask + shadow));
            }
            ENDCG
        }
    }
}
