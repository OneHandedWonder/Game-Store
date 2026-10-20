BEGIN;

-- Sample prices and stock for development, not live retail data.
INSERT INTO products (id, name, description, price, stock, image_url, is_active)
VALUES
    ('ca7a1000-0000-4000-8000-000000000001', 'Minecraft - PC', 'Sandbox building and survival game for PC.', 199.95, 25, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000002', 'Stardew Valley - Nintendo Switch', 'Farming and village-life game for Nintendo Switch.', 249.95, 12, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000003', 'Elden Ring - PlayStation 5', 'Open-world action RPG for PlayStation 5.', 399.95, 8, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000004', 'Forza Horizon 5 - Xbox Series X', 'Open-world racing game for Xbox Series X.', 349.95, 10, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000005', 'PlayStation 5 Console', 'Home console with disc drive and one controller.', 3999.00, 4, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000006', 'Nintendo Switch OLED Console', 'Nintendo Switch OLED console with dock and Joy-Con controllers.', 2499.00, 6, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000007', 'Wireless Controller', 'Wireless controller for PC gaming.', 449.95, 15, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000008', 'Gaming Headset', 'Wired stereo headset with microphone and 3.5 mm connector.', 299.95, 18, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000009', 'Mechanical Keyboard', 'USB mechanical keyboard with a Danish layout.', 599.95, 7, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000010', 'Gaming Mouse', 'Wired USB gaming mouse with adjustable sensitivity.', 199.95, 20, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000011', 'Console Carrying Case', 'Protective carrying case for a handheld console. Currently out of stock.', 149.95, 0, NULL, true),
    ('ca7a1000-0000-4000-8000-000000000012', 'Retro Controller', 'Discontinued USB controller, retained as an inactive catalog example.', 99.95, 2, NULL, false)
ON CONFLICT (id) DO NOTHING;

COMMIT;
