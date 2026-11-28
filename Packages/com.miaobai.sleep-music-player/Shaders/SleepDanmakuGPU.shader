Shader "SleepSanctuary/DanmakuGPU"
{
    Properties
    {
        [PerRendererData] _MainTex ("Font Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (0,0,0,0.88)
        _OutlineWidth ("Outline Pixel Width", Range(0.5, 3.0)) = 1.6
        _CurveAngle ("曲面屏总弧度 (rad)", Range(-1.58, 1.58)) = 0.0
        _StageWidth ("舞台画布宽度 (px)", Float) = 1920.0
        _StageHeight ("舞台画布高度 (px)", Float) = 1080.0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+3"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 canvasXY : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            fixed4 _OutlineColor;
            float _OutlineWidth;
            float _CurveAngle;
            float _StageWidth;
            float _StageHeight;

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 p = v.vertex.xyz;
                o.canvasXY = p.xy;
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
                o.vertex = UnityObjectToClipPos(float4(p, 1.0));
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 严格裁剪在屏幕画布矩形内（兼容平面与任意弧度曲面屏，防止弹幕飘出屏幕外）
                float halfW = max(100.0, _StageWidth * 0.5);
                float halfH = max(100.0, _StageHeight * 0.5);
                if (abs(i.canvasXY.x) > halfW || abs(i.canvasXY.y) > halfH) discard;

                float centerA = tex2D(_MainTex, i.texcoord).a;

                // 4-tap GPU 片段描边，彻底替代 UnityEngine.UI.Outline（节省 80% 顶点数，1 DrawCall 批处理）
                float2 off = _MainTex_TexelSize.xy * _OutlineWidth;
                float outA = max(
                    max(tex2D(_MainTex, i.texcoord + float2( off.x,  off.y)).a,
                        tex2D(_MainTex, i.texcoord + float2(-off.x,  off.y)).a),
                    max(tex2D(_MainTex, i.texcoord + float2( off.x, -off.y)).a,
                        tex2D(_MainTex, i.texcoord + float2(-off.x, -off.y)).a)
                );

                float finalA = max(centerA, outA * _OutlineColor.a) * i.color.a;
                if (finalA < 0.01) discard;

                fixed3 rgb = lerp(_OutlineColor.rgb, i.color.rgb, saturate(centerA * 1.15));
                return fixed4(rgb, finalA);
            }
            ENDCG
        }
    }
}
