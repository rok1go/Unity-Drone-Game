// Full-screen blur for the concussion effect: a 9-tap box around each pixel, widened by _Spread,
// mixed with the sharp image by _Amount, with a light wash towards grey.
Shader "Hidden/Drone Game/Concussion Blur"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Spread ("Spread (pixels)", Float) = 2
        _Amount ("Amount", Range(0, 1)) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Spread;
            float _Amount;

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 sharp = tex2D(_MainTex, i.uv);
                float2 o = _MainTex_TexelSize.xy * _Spread;
                fixed4 sum = sharp;
                sum += tex2D(_MainTex, i.uv + float2( o.x, 0));
                sum += tex2D(_MainTex, i.uv + float2(-o.x, 0));
                sum += tex2D(_MainTex, i.uv + float2(0,  o.y));
                sum += tex2D(_MainTex, i.uv + float2(0, -o.y));
                sum += tex2D(_MainTex, i.uv + o);
                sum += tex2D(_MainTex, i.uv - o);
                sum += tex2D(_MainTex, i.uv + float2(o.x, -o.y));
                sum += tex2D(_MainTex, i.uv + float2(-o.x, o.y));
                fixed4 blurred = sum / 9;
                fixed grey = dot(blurred.rgb, fixed3(0.299, 0.587, 0.114));
                blurred.rgb = lerp(blurred.rgb, grey.xxx, 0.25 * _Amount);
                return lerp(sharp, blurred, _Amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
