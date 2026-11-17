// Same integer rule as EmissionScheduler.AcceptBurst. The carry is -Debt, always in (-65536,0].
// 32-bit limbs preserve count*q exactly without shaderInt64; all event and explicit bursts use this owner.
uint acceptBurst(uint count, uint q, inout uint debt)
{
    uint lowProduct = (count & 0xffffu) * q;
    uint highProduct = (count >> 16u) * q;
    uint whole = (lowProduct >> 16u) + (highProduct & 0xffffu);
    uint high = (highProduct >> 16u) + (whole >> 16u);
    uint low = (whole << 16u) | (lowProduct & 0xffffu);
    if (high == 0u && low <= debt) { debt -= low; return 0u; }
    if (low < debt) high--;
    low -= debt;
    uint requested = (high << 16u) | (low >> 16u);
    uint fraction = low & 0xffffu;
    if (fraction != 0u) requested++;
    debt = (0u - fraction) & 0xffffu;
    return requested;
}
