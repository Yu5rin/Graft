def validate_order(order):
    if order is None:
        return False
    if not order.items:
        return False
    if order.total <= 0:
        return False
    return True
