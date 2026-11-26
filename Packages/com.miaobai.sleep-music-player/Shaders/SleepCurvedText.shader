// 副歌词、翻译、歌名与歌手通用曲面文字着色器：支持平面与任意弧度曲面屏自动贴合弯曲。
Shader "SleepSanctuary/GPU/CurvedUIText"
{
    Properties
    {
        [PerRendererData] _MainTex ("字体纹理", 2D) = "white" {}
        _Color ("基础色调", Color) = (1, 1, 1, 1)
        _CurveAngle ("曲面屏总弧度 (rad)", Range(-1.58, 1.58)) = 0.0
        _StageWidth ("舞台画布宽度 (px)", Float) = 1920.0
    }

    SubShader
    {
        Tags { "Queue"="Transparent+2" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Cull Off Lighting Off ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv    : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _CurveAngle;
            float _StageWidth;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 p = v.vertex.xyz;
                if (abs(_CurveAngle) > 0.001)
                {
                    float halfW = max(100.0, _StageWidth * 0.5);
                    float halfAng = abs(_CurveAngle) * 0.5;
                    float radius = halfW / max(0.001, halfAng);
                    float sgn = _CurveAngle >= 0.0 ? 1.0 : -1.0;
                    float theta = (p.x / halfW) * halfAng;
                    p.x = radius * sin(theta);
                    p.z -= sgn * radius * (1.0 - cos(theta));
                }

                o.pos = UnityObjectToClipPos(float4(p, 1.0));
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed a = tex2D(_MainTex, i.uv).a;
                return fixed4(i.color.rgb, a * i.color.a);
            }
            ENDCG
        }
    }
}
