Shader "SleepSanctuary/DanmakuGPU"
{
    Properties
    {
        [PerRendererData] _MainTex ("Font Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (0,0,0,0.88)
        _OutlineWidth ("Outline Pixel Width", Range(0.5, 3.0)) = 1.6
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _Color;
            fixed4 _OutlineColor;
            float _OutlineWidth;

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
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
